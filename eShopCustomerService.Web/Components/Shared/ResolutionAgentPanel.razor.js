const PREPARE_FOR_PAUSE = "PrepareForPause";
const conversations = new Set();

export async function prepareConversationsForPause(signal) {
    if (signal.aborted) return;
    await Promise.all([...conversations].map(async tracker => {
        try {
            await tracker.prepareForPause();
        } catch (error) {
            console.error("The agent could not prepare its conversation for pause.", error);
            throw error;
        }
    }));
}

export function disposeConversations() {
    for (const tracker of [...conversations]) tracker.dispose();
}

export function initialize(panel, conversation) {
    const transcript = panel.querySelector(".sc-ai-chat-page__body");
    if (!transcript) {
        throw new Error("The agent transcript must exist before scroll tracking starts.");
    }

    let follow = true;
    const onScroll = () => {
        follow = transcript.scrollHeight - transcript.clientHeight - transcript.scrollTop <= 40;
    };
    transcript.addEventListener("scroll", onScroll, { passive: true });
    const observer = new MutationObserver(() => {
        if (follow) {
            transcript.scrollTop = transcript.scrollHeight;
        }
    });
    observer.observe(transcript, { childList: true, subtree: true, characterData: true });
    transcript.scrollTop = transcript.scrollHeight;

    const tracker = {
        prepareForPause() {
            return conversation.invokeMethodAsync(PREPARE_FOR_PAUSE);
        },
        dispose() {
            observer.disconnect();
            transcript.removeEventListener("scroll", onScroll);
            conversations.delete(tracker);
        }
    };
    conversations.add(tracker);
    return tracker;
}
