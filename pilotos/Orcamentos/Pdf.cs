using System.Globalization;
using System.Text;

internal static class Pdf
{
    public static byte[] Create(Quote quote)
    {
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        string Money(long cents) => (cents / 100m).ToString("C", culture).Replace('\u00a0', ' ');
        var content = new StringBuilder("0.28 0.16 0.13 rg 0 744 595 98 re f\n");
        Add(56, 786, 22, "Oficina Horizonte", "1 0.95 0.85");
        Add(56, 693, 26, $"Orçamento #{quote.Id}");
        Add(56, 646, 12, "CLIENTE");
        AddWrapped(626, quote.Client);
        Add(56, 559, 12, "ITEM OU SERVIÇO");
        AddWrapped(539, quote.Item);
        Add(56, 472, 12, $"Quantidade {quote.Quantity}   ×   {Money(quote.Unit)}");
        Add(56, 419, 19, $"Total   {Money(quote.Total)}");
        var stream = content.ToString();
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(stream)} >>\nstream\n{stream}endstream"
        };
        var pdf = new StringBuilder("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());

        void AddWrapped(int y, string value)
        {
            const int max = 39;
            while (value.Length > max)
            {
                var space = value.LastIndexOf(' ', max);
                var boundary = space >= max / 2 ? space : max;
                Add(56, y, 12, value[..boundary]);
                value = value[boundary..].TrimStart();
                y -= 16;
            }
            Add(56, y, 12, value);
        }
        void Add(int x, int y, int size, string text, string color = "0.18 0.12 0.09")
        {
            var escaped = text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
            content.Append($"BT {color} rg /F1 {size} Tf {x} {y} Td ({escaped}) Tj ET\n");
        }
    }
}
