const token = location.hash.slice(1);
if (/^[0-9a-f]{64}$/.test(token)) {
    history.replaceState(null, "", "/entrar");
    fetch("/entrar", { method: "POST", headers: { "X-Central-Bootstrap": token } })
        .then(response => {
            if (response.ok) location.replace("/");
            else document.querySelector("#estado").textContent = "A sessão expirou. Reinicie a Central pelo launcher.";
        })
        .catch(() => document.querySelector("#estado").textContent = "A Central não respondeu.");
}
