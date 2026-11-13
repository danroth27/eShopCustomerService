export function initialize(panel) {
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

    return {
        dispose() {
            observer.disconnect();
            transcript.removeEventListener("scroll", onScroll);
        }
    };
}
