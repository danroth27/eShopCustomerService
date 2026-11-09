using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using eShopCustomerService.Web.Domain;
using Microsoft.Data.Sqlite;

namespace eShopCustomerService.Web.Data;

public sealed class ReturnStore : IDisposable
{
    private readonly string connectionString;
    private readonly SemaphoreSlim writer = new(1, 1);

    public ReturnStore(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var path = configuration["Demo:DatabasePath"]
            ?? Path.Combine(environment.ContentRootPath, "App_Data", "returns.db");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString();
    }

    public async Task InitializeAsync()
    {
        await using var db = await OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Orders (
                Id INTEGER PRIMARY KEY, Customer TEXT NOT NULL, Company TEXT NOT NULL,
                Email TEXT NOT NULL, Product TEXT NOT NULL, ProductCode TEXT NOT NULL,
                PaidCents INTEGER NOT NULL, Address TEXT NOT NULL, Reason TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'Open', ResolutionJson TEXT);
            CREATE TABLE IF NOT EXISTS Inventory (
                ProductCode TEXT PRIMARY KEY, Name TEXT NOT NULL, Description TEXT NOT NULL,
                Available INTEGER NOT NULL CHECK(Available >= 0));
            CREATE TABLE IF NOT EXISTS Drafts (
                Id TEXT PRIMARY KEY, OrderId INTEGER NOT NULL REFERENCES Orders(Id),
                ResolutionJson TEXT NOT NULL, CreatedAt TEXT NOT NULL, ConfirmedAt TEXT);
            CREATE TABLE IF NOT EXISTS Audit (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, OrderId INTEGER NOT NULL REFERENCES Orders(Id),
                Actor TEXT NOT NULL, Action TEXT NOT NULL, Timestamp TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS CustomerRequests (
                Reference TEXT PRIMARY KEY, OrderId INTEGER NOT NULL REFERENCES Orders(Id),
                Name TEXT NOT NULL, Email TEXT NOT NULL, Message TEXT NOT NULL, CreatedAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS DemoPermissions (
                SessionId TEXT PRIMARY KEY, RefundApproval INTEGER NOT NULL CHECK(RefundApproval IN (0, 1)),
                ExpiresAtUtc INTEGER NOT NULL);
            """;
        await command.ExecuteNonQueryAsync();
        command.CommandText = "SELECT COUNT(*) FROM Orders";
        if ((long)(await command.ExecuteScalarAsync())! == 0)
        {
            await SeedAsync(db);
        }
    }

    public async Task CreateDemoPermissionAsync(
        string sessionId, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        await writer.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);
            await using var command = db.CreateCommand();
            command.CommandText = """
                DELETE FROM DemoPermissions WHERE ExpiresAtUtc <= $now;
                INSERT INTO DemoPermissions VALUES ($session, 1, $expires);
                """;
            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$expires", expiresAt.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            writer.Release();
        }
    }

    public async Task<bool?> RefreshDemoPermissionAsync(
        string sessionId, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        await writer.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);
            await using var command = db.CreateCommand();
            command.CommandText = """
                UPDATE DemoPermissions SET ExpiresAtUtc = $expires
                WHERE SessionId = $session AND ExpiresAtUtc > $now
                RETURNING RefundApproval;
                """;
            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$expires", expiresAt.ToUnixTimeMilliseconds());
            return await command.ExecuteScalarAsync(ct) is long allowed ? allowed != 0 : null;
        }
        finally
        {
            writer.Release();
        }
    }

    public async Task<bool> SetDemoRefundApprovalAsync(
        string sessionId, bool allowed, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        await writer.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);
            await using var command = db.CreateCommand();
            command.CommandText = """
                UPDATE DemoPermissions SET RefundApproval = $allowed, ExpiresAtUtc = $expires
                WHERE SessionId = $session AND ExpiresAtUtc > $now;
                """;
            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$allowed", allowed ? 1 : 0);
            command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$expires", expiresAt.ToUnixTimeMilliseconds());
            return await command.ExecuteNonQueryAsync(ct) == 1;
        }
        finally
        {
            writer.Release();
        }
    }

    public async Task RemoveDemoPermissionAsync(string sessionId, CancellationToken ct = default)
    {
        await writer.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);
            await using var command = db.CreateCommand();
            command.CommandText = "DELETE FROM DemoPermissions WHERE SessionId = $session";
            command.Parameters.AddWithValue("$session", sessionId);
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            writer.Release();
        }
    }

    public async Task<IReadOnlyList<ReturnCase>> GetCasesAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT * FROM Orders ORDER BY Id";
        await using var reader = await command.ExecuteReaderAsync(ct);
        List<ReturnCase> cases = [];
        while (await reader.ReadAsync(ct))
        {
            cases.Add(ReadCase(reader));
        }
        return cases;
    }

    public async Task<ReturnCase?> GetCaseAsync(int orderId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return await FindCaseAsync(db, null, orderId, ct);
    }

    public async Task<IReadOnlyList<InventoryItem>> GetInventoryAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT ProductCode, Name, Description, Available FROM Inventory ORDER BY ProductCode";
        await using var reader = await command.ExecuteReaderAsync(ct);
        List<InventoryItem> products = [];
        while (await reader.ReadAsync(ct))
        {
            products.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        }
        return products;
    }

    public async Task<IReadOnlyList<AuditEntry>> GetAuditAsync(int orderId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT Id, Actor, Action, Timestamp FROM Audit WHERE OrderId = $order ORDER BY Id DESC";
        command.Parameters.AddWithValue("$order", orderId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        List<AuditEntry> entries = [];
        while (await reader.ReadAsync(ct))
        {
            entries.Add(new(reader.GetInt64(0), orderId, reader.GetString(1), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
        }
        return entries;
    }

    public async Task<ResolutionDraft?> GetDraftAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return await FindDraftAsync(db, null, id, ct);
    }

    public async Task<ResolutionResult<CustomerRequestReceipt>> SubmitCustomerRequestAsync(
        CustomerReturnRequest request, CancellationToken ct = default)
    {
        var normalized = new CustomerReturnRequest
        {
            OrderId = request.OrderId,
            Name = request.Name?.Trim() ?? "",
            Email = request.Email?.Trim() ?? "",
            Message = request.Message?.Trim() ?? ""
        };
        List<ValidationResult> errors = [];
        if (!Validator.TryValidateObject(normalized, new ValidationContext(normalized), errors, validateAllProperties: true))
        {
            return new(new ResolutionFailure(
                string.Join(" ", errors.Select(error => error.ErrorMessage)), ResolutionFailureKind.Invalid));
        }

        await writer.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);
            using var transaction = db.BeginTransaction();
            var order = await FindCaseAsync(db, transaction, normalized.OrderId, ct);
            if (order is null)
            {
                return new(new ResolutionFailure("The order was not found.", ResolutionFailureKind.NotFound));
            }
            if (order.Status != "Open")
            {
                return new(new ResolutionFailure("This return has already been resolved. Contact our care team for further help."));
            }

            var receipt = new CustomerRequestReceipt($"CARE-{Guid.NewGuid():N}".ToUpperInvariant(), order.OrderId);
            await using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO CustomerRequests (Reference, OrderId, Name, Email, Message, CreatedAt)
                VALUES ($reference, $order, $name, $email, $message, $created);
                UPDATE Orders SET Customer = $name, Email = $email, Reason = $message WHERE Id = $order;
                """;
            command.Parameters.AddWithValue("$reference", receipt.Reference);
            command.Parameters.AddWithValue("$order", receipt.OrderId);
            command.Parameters.AddWithValue("$name", normalized.Name);
            command.Parameters.AddWithValue("$email", normalized.Email);
            command.Parameters.AddWithValue("$message", normalized.Message);
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
            await AddAuditAsync(db, transaction, order.OrderId, normalized.Name,
                $"Customer return request {receipt.Reference} received: {normalized.Message}", ct);
            transaction.Commit();
            return new(receipt);
        }
        finally
        {
            writer.Release();
        }
    }

    public async Task<ResolutionResult<ResolutionDraft>> SaveDraftAsync(
        SaveResolutionRequest request, string actor, CancellationToken ct = default)
    {
        await writer.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);
            using var transaction = db.BeginTransaction();
            var order = await FindCaseAsync(db, transaction, request.OrderId, ct);
            if (order is null)
            {
                return new(new ResolutionFailure("The order was not found.", ResolutionFailureKind.NotFound));
            }
            if (order.Status != "Open")
            {
                return new(new ResolutionFailure("This return has already been resolved."));
            }
            if (await ValidateResolutionAsync(db, transaction, order, request.Resolution, ct) is { } failure)
            {
                return new(failure);
            }
            var draft = new ResolutionDraft(Guid.NewGuid(), order.OrderId, request.Resolution, DateTimeOffset.UtcNow, null);
            await using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO Drafts VALUES ($id, $order, $json, $created, NULL)";
            command.Parameters.AddWithValue("$id", draft.Id.ToString());
            command.Parameters.AddWithValue("$order", order.OrderId);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(request.Resolution, JsonSerializerOptions.Web));
            command.Parameters.AddWithValue("$created", draft.CreatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
            await AddAuditAsync(db, transaction, order.OrderId, actor, "Resolution draft prepared", ct);
            transaction.Commit();
            return new(draft);
        }
        finally
        {
            writer.Release();
        }
    }

    public async Task<ResolutionResult<ReturnCase>> ConfirmAsync(Guid id, string actor, CancellationToken ct = default)
    {
        await writer.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);
            using var transaction = db.BeginTransaction();
            var draft = await FindDraftAsync(db, transaction, id, ct);
            if (draft is null)
            {
                return new(new ResolutionFailure("The resolution draft was not found.", ResolutionFailureKind.NotFound));
            }
            var order = await FindCaseAsync(db, transaction, draft.OrderId, ct);
            if (order is null)
            {
                return new(new ResolutionFailure("The order was not found.", ResolutionFailureKind.NotFound));
            }
            if (draft.ConfirmedAt is not null)
            {
                return new(order);
            }
            if (order.Status != "Open")
            {
                return new(new ResolutionFailure("A different resolution has already been confirmed for this return."));
            }
            if (await ValidateResolutionAsync(db, transaction, order, draft.Resolution, ct) is { } failure)
            {
                return new(failure);
            }

            if (draft.Resolution.Value is ReplacementResolution replacement)
            {
                await using var allocation = db.CreateCommand();
                allocation.Transaction = transaction;
                allocation.CommandText = "UPDATE Inventory SET Available = Available - 1 WHERE ProductCode = $code AND Available > 0";
                allocation.Parameters.AddWithValue("$code", replacement.ProductCode);
                if (await allocation.ExecuteNonQueryAsync(ct) != 1)
                {
                    return new(new ResolutionFailure("Inventory changed. Choose an available replacement."));
                }
            }

            var json = JsonSerializer.Serialize(draft.Resolution, JsonSerializerOptions.Web);
            await using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE Orders SET Status = 'Resolved', ResolutionJson = $json WHERE Id = $order;
                UPDATE Drafts SET ConfirmedAt = $now WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$order", order.OrderId);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id.ToString());
            await command.ExecuteNonQueryAsync(ct);
            var action = draft.Resolution.Value is RefundResolution
                ? "Simulated refund confirmed; no payment was issued"
                : "Replacement confirmed and one unit allocated; no shipment was created";
            await AddAuditAsync(db, transaction, order.OrderId, actor, action, ct);
            transaction.Commit();
            return new(order with { Status = "Resolved", Resolution = draft.Resolution });
        }
        finally
        {
            writer.Release();
        }
    }

    public async Task ResetAsync(CancellationToken ct = default)
    {
        await writer.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct);
            await SeedAsync(db);
        }
        finally
        {
            writer.Release();
        }
    }

    private static async Task<ResolutionFailure?> ValidateResolutionAsync(
        SqliteConnection db, SqliteTransaction transaction, ReturnCase order, Resolution resolution, CancellationToken ct)
    {
        switch (resolution.Value)
        {
            case RefundResolution refund when refund.Amount == order.PaidAmount
                && refund.PaymentReference == $"PAY-{order.OrderId}":
                return null;
            case RefundResolution:
                return new("A full refund must match the original payment and paid amount.", ResolutionFailureKind.Invalid);
            case ReplacementResolution replacement:
                if (string.IsNullOrWhiteSpace(replacement.DeliveryAddress)
                    || replacement.DeliveryAddress.Length is < 5 or > 240)
                {
                    return new("Provide a delivery address between 5 and 240 characters.", ResolutionFailureKind.Invalid);
                }
                await using (var check = db.CreateCommand())
                {
                    check.Transaction = transaction;
                    check.CommandText = "SELECT Available FROM Inventory WHERE ProductCode = $code";
                    check.Parameters.AddWithValue("$code", replacement.ProductCode ?? "");
                    var value = await check.ExecuteScalarAsync(ct);
                    if (value is not long available || available <= 0)
                    {
                        return new("This replacement is unavailable. Choose an available alternative.");
                    }
                }
                return null;
            default:
                return new("Choose a refund or replacement resolution.", ResolutionFailureKind.Invalid);
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var db = new SqliteConnection(connectionString);
        try
        {
            await db.OpenAsync(ct);
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    private static ReturnCase ReadCase(SqliteDataReader reader) => new(
        reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetInt64(6) / 100m, reader.GetString(7),
        reader.GetString(8), reader.GetString(9),
        reader.IsDBNull(10) ? (Resolution?)null : JsonSerializer.Deserialize<Resolution>(reader.GetString(10), JsonSerializerOptions.Web));

    private static async Task<ReturnCase?> FindCaseAsync(
        SqliteConnection db, SqliteTransaction? transaction, int orderId, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM Orders WHERE Id = $order";
        command.Parameters.AddWithValue("$order", orderId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadCase(reader) : null;
    }

    private static async Task<ResolutionDraft?> FindDraftAsync(
        SqliteConnection db, SqliteTransaction? transaction, Guid id, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT OrderId, ResolutionJson, CreatedAt, ConfirmedAt FROM Drafts WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return new(id, reader.GetInt32(0),
            JsonSerializer.Deserialize<Resolution>(reader.GetString(1), JsonSerializerOptions.Web),
            DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
            reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture));
    }

    private static async Task AddAuditAsync(
        SqliteConnection db, SqliteTransaction transaction, int orderId, string actor, string action, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Audit (OrderId, Actor, Action, Timestamp) VALUES ($order, $actor, $action, $now)";
        command.Parameters.AddWithValue("$order", orderId);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task SeedAsync(SqliteConnection db)
    {
        using var transaction = db.BeginTransaction();
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM CustomerRequests;
            DELETE FROM Audit;
            DELETE FROM Drafts;
            DELETE FROM Orders;
            DELETE FROM Inventory;
            INSERT INTO Inventory VALUES
                ('DOCK-PRO', 'USB-C Dock Pro', 'Original model / dual 4K / 90W charging', 0),
                ('DOCK-PRO-PLUS', 'USB-C Dock Pro+', 'Compatible upgrade / dual 4K / 100W charging', 12);
            INSERT INTO Orders (Id, Customer, Company, Email, Product, ProductCode, PaidCents, Address, Reason) VALUES
                (48021, 'Alex Morgan', 'Northstar Design', 'alex@example.com', 'USB-C Dock Pro', 'DOCK-PRO', 14900,
                 '120 Market Street, Seattle, WA 98101', 'External displays disconnect intermittently. Customer requested an exchange.'),
                (48022, 'Jordan Lee', 'Contoso Studio', 'jordan@example.com', 'USB-C Dock Pro', 'DOCK-PRO', 14900,
                 '75 Pine Street, Portland, OR 97204', 'Incorrect model ordered. Customer prefers a refund.'),
                (48023, 'Sam Rivera', 'Fabrikam Labs', 'sam@example.com', 'USB-C Dock Pro', 'DOCK-PRO', 14900,
                 '220 Lake Avenue, Bellevue, WA 98004', 'Power adapter is not working. Replacement requested.');
            INSERT INTO Audit (OrderId, Actor, Action, Timestamp) VALUES
                (48021, 'eShop Orders (seed)', 'Order delivered', '2026-10-22T09:30:00+00:00'),
                (48021, 'Customer (seed)', 'Return requested: intermittent display connection', '2026-10-28T15:20:00+00:00'),
                (48021, 'Customer service (seed)', 'Return approved within the 30-day policy', '2026-10-29T08:45:00+00:00');
            """;
        await command.ExecuteNonQueryAsync();
        transaction.Commit();
    }

    public void Dispose() => writer.Dispose();
}
