using System.Text.Json.Nodes;
using Orbita.Core;

namespace Orbita.Studio;

internal static class SignalActivity
{
    private const string LegacySeparator = "\n\nContexto do pedido no Signal:\n";

    public static IResult Read(string id, string? cursor, string? requestId, ICheckpointStore checkpoints)
    {
        var mission = checkpoints.Get(id);
        var events = checkpoints.EventsAfter(id, 0);
        var revision = $"{events.LastOrDefault()?.Sequence ?? 0}:{mission.State}";
        if (revision == cursor) return Results.Json(new { cursor = revision });

        var starts = events.Select((entry, index) => (entry, index)).Where(row => row.entry.Kind == "usuario").ToArray();
        var turns = new List<object>();
        for (var i = 0; i < starts.Length; i++)
        {
            var (start, index) = starts[i];
            var legacy = start.Text.LastIndexOf(LegacySeparator, StringComparison.Ordinal);
            var metadata = start.Data as JsonObject;
            var receipt = (string?)metadata?["requestId"];
            var source = (string?)metadata?["source"];
            if (requestId != null ? requestId != receipt : legacy < 0 && source is not ("signal" or "discord")) continue;

            var latest = i == starts.Length - 1;
            var end = latest ? events.Count : starts[i + 1].index;
            var history = events.Skip(index + 1).Take(end - index - 1).ToArray();
            var reply = string.Concat(history.Where(entry => entry.Kind == "mensagem").Select(entry => entry.Text));
            var lastStatus = history.LastOrDefault(entry => entry.Kind == "estado");
            var terminalState = (string?)(lastStatus?.Data as JsonObject)?["terminalState"];
            var active = terminalState == null && latest && mission.State is "executando" or "conversando";
            if (!active && latest && receipt != null) terminalState ??= mission.State;
            var detail = lastStatus?.Text;
            var tool = active ? history.LastOrDefault(entry => entry.Kind == "ferramenta") : null;
            if (tool != null)
            {
                detail = tool.Text.StartsWith("File ", StringComparison.Ordinal) ? $"Arquivo {Path.GetFileName(tool.Text[5..])}" :
                    tool.Text == "Run update_steps" ? "Atualizando o plano" : tool.Text;
            }
            turns.Add(new { sequence = start.Sequence, startedAt = start.At, endedAt = active ? (DateTimeOffset?)null : history.LastOrDefault()?.At ?? start.At,
                recordedText = start.Text, text = legacy < 0 ? start.Text : start.Text[(legacy + LegacySeparator.Length)..].Trim(),
                attachments = metadata?["attachments"], missionId = id, requestId = receipt, source = source ?? "signal", terminalState, reply, active, detail });
        }
        return Results.Json(new { cursor = revision, turns });
    }
}
