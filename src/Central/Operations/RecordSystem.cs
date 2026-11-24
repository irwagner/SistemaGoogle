using System.Net;
using System.Text.Json;
using Central.Domain;

namespace Central.Operations;

internal sealed record RecordField(string Label, string Type, bool Required);
internal sealed record RecordDefinition(bool Suitable, string Title, string Description, bool Completion, RecordField[] Fields);

internal static class RecordSystem
{
    internal const string Instructions = """
        Responda somente JSON, conforme este exemplo:
        {"suitable":true,"title":"Caderno de Tarefas","description":"Organize atividades locais.","completion":true,
        "fields":[{"label":"Nova tarefa","type":"text","required":true}]}
        Decida se todos os requisitos aprovados cabem num cadastro de registros locais com formulário,
        campos de texto/número/data, adicionar, pesquisar, marcar conclusão opcional, excluir e guardar no navegador.
        Se forem necessários relações, serviços, integrações, cálculos ou outros fluxos fora desse escopo, retorne {"suitable":false}.
        Para suitable true, use o título e os rótulos pedidos, tipos text, number ou date, e até 8 campos.
        """;

    internal static string? Render(string response, string work)
    {
        RecordDefinition? definition;
        try { definition = JsonSerializer.Deserialize<RecordDefinition>(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return null; }
        if (definition == null || !definition.Suitable) return null;
        var title = Store.Text(definition.Title, 100, "o título do sistema");
        var overview = Store.Text(definition.Description, 300, "a descrição do sistema");
        if (definition.Fields is not { Length: > 0 and <= 8 } || definition.Fields.Any(f => f == null || f.Type is not ("text" or "number" or "date")))
            return null;
        var fields = definition.Fields.Select(f => f with { Label = Store.Text(f.Label, 80, "o rótulo") }).ToArray();
        var model = Store.Json(new { fields, definition.Completion, key = "central-registros-" + work });
        return $$"""
            <!doctype html>
            <html lang="pt-BR">
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; form-action 'none'; base-uri 'none'">
            <title>{{WebUtility.HtmlEncode(title)}}</title>
            <style>
            :root{color:#173732;background:#eff4ec;font:16px/1.55 system-ui,sans-serif}
            body{max-width:56rem;margin:auto;padding:2rem 1rem}
            header{border-bottom:1px solid #abbfaf;padding-bottom:1rem}
            h1{font:clamp(2rem,6vw,3.4rem) Georgia;margin:.4em 0 .2em}
            section{background:#fff;border:1px solid #c3d2c3;border-radius:.7rem;margin:1.2rem 0;padding:1rem}
            form{display:grid;gap:.7rem}label{display:grid;gap:.25rem}input,button{font:inherit}
            input:not([type=checkbox]){padding:.45rem;border:1px solid #879f91;border-radius:.3rem}
            button{width:fit-content;padding:.45rem .85rem;background:#1a5b47;color:#fff;border:0;border-radius:.35rem}
            ul{list-style:none;margin:0;padding:0}li{display:flex;flex-wrap:wrap;gap:.75rem;align-items:center;border-top:1px solid #e0e9dc;padding:.65rem 0}
            li p{flex:1;overflow-wrap:anywhere;margin:0}li button{background:#f1f5ed;color:#1d4f41;border:1px solid #9eb1a3}
            input:focus-visible,button:focus-visible{outline:2px solid #ac7022;outline-offset:2px}
            </style>
            <body>
            <header><h1>{{WebUtility.HtmlEncode(title)}}</h1><p>{{WebUtility.HtmlEncode(overview)}}</p></header>
            <main>
            <section><h2>Novo registro</h2><form id="novo"><button>Adicionar</button></form></section>
            <section><h2>Registros</h2><label>Pesquisar<input id="busca" type="search"></label><p id="aviso" aria-live="polite"></p><ul id="lista"></ul></section>
            </main>
            <script>
            const setup = {{model}};
            const form = document.getElementById("novo");
            const search = document.getElementById("busca");
            const list = document.getElementById("lista");
            const status = document.getElementById("aviso");
            const fields = setup.fields.map(field => {
                const label = document.createElement("label");
                label.textContent = field.label;
                const input = document.createElement("input");
                input.type = field.type;
                input.required = field.required;
                if (field.type === "text") input.maxLength = 240;
                if (field.type === "number") input.step = "any";
                label.append(input);
                form.insertBefore(label, form.lastElementChild);
                return input;
            });
            let records = [];
            try {
                const saved = JSON.parse(localStorage.getItem(setup.key) || "[]");
                if (Array.isArray(saved)) records = saved.filter(entry => Array.isArray(entry.values));
            } catch { status.textContent = "Os registros anteriores não puderam ser carregados."; }
            function commit(next) {
                try {
                    localStorage.setItem(setup.key, JSON.stringify(next));
                    records = next;
                    draw();
                    return true;
                } catch {
                    status.textContent = "Não foi possível salvar no navegador.";
                    return false;
                }
            }
            function draw() {
                list.replaceChildren();
                const query = search.value.trim().toLocaleLowerCase("pt-BR");
                const filtered = records.filter(entry => entry.values.join(" ").toLocaleLowerCase("pt-BR").includes(query));
                status.textContent = filtered.length ? filtered.length + " registro(s)" : "Nenhum registro encontrado.";
                for (const entry of filtered) {
                    const row = document.createElement("li");
                    const text = document.createElement("p");
                    text.textContent = entry.values.filter(Boolean).join(" · ");
                    if (setup.completion) {
                        const checkbox = document.createElement("input");
                        checkbox.type = "checkbox";
                        checkbox.checked = entry.done;
                        checkbox.setAttribute("aria-label", "Concluir " + text.textContent);
                        checkbox.addEventListener("change", () => {
                            if (!commit(records.map(record => record === entry ? { ...record, done: checkbox.checked } : record)))
                                checkbox.checked = entry.done;
                        });
                        row.append(checkbox);
                    }
                    const remove = document.createElement("button");
                    remove.type = "button";
                    remove.textContent = "Excluir";
                    remove.addEventListener("click", () => {
                        if (confirm("Excluir este registro?")) commit(records.filter(record => record !== entry));
                    });
                    row.append(text, remove);
                    list.append(row);
                }
            }
            form.addEventListener("submit", event => {
                event.preventDefault();
                const values = fields.map(input => input.value.trim());
                if (!values.some(Boolean)) { status.textContent = "Preencha pelo menos um campo."; return; }
                if (commit([...records, { values, done: false }])) form.reset();
            });
            search.addEventListener("input", draw);
            draw();
            </script>
            </body></html>
            """;
    }
}
