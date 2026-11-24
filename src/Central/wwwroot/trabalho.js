const workspace = document.querySelector("[data-workspace]");
if (workspace) {
    const endpoint = handler => location.pathname + "?handler=" + handler;
    for (const field of workspace.querySelectorAll("[data-draft]")) {
        let timer;
        const form = field.closest("form");
        const status = form.querySelector("[data-save-status]");
        const save = async () => {
            const body = new FormData();
            body.set("__RequestVerificationToken", form.elements.__RequestVerificationToken.value);
            body.set("revision", workspace.dataset.revision);
            body.set("field", field.dataset.draft);
            body.set("body", field.value);
            try {
                const response = await fetch(endpoint("Rascunho"), { method: "POST", body, keepalive: true });
                status.textContent = response.ok ? "Rascunho salvo" : "O trabalho mudou. Atualize para continuar.";
            } catch {
                status.textContent = "Rascunho pendente. Use Salvar para tentar novamente.";
            }
        };
        field.addEventListener("input", () => {
            status.textContent = "Salvando rascunho…";
            clearTimeout(timer);
            timer = setTimeout(save, 500);
        });
        field.addEventListener("change", () => { clearTimeout(timer); void save(); });
    }
    if (workspace.dataset.live === "true") {
        const navigation = window.setInterval(async () => {
            try {
                const response = await fetch(endpoint("Estado"));
                if (!response.ok) return;
                const state = await response.json();
                if (String(state.revision) !== workspace.dataset.revision && !document.activeElement?.matches("textarea,input,select")) {
                    clearInterval(navigation);
                    location.reload();
                }
            } catch { /* Retry on the next tick. */ }
        }, 1800);
    }
}
