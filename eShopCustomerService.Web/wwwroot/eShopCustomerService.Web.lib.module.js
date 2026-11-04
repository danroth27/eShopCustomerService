export function afterWebStarted(blazor) {
    const theme = document.documentElement.getAttribute("data-theme");

    blazor.addEventListener("enhancedload", () => {
        document.documentElement.setAttribute("data-theme", theme);
    });
}
