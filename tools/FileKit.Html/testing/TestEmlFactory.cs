using System.Text;

namespace FileKit.Html.Testing;

internal static class TestEmlFactory
{
    internal static byte[] Plain(string text = "Grüße\nSecond line") => Encoding.ASCII.GetBytes(Headers + "Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "\r\n");
    internal static byte[] Html(byte[]? png = null, string extra = "", bool attachments = true)
    {
        string html = "<html><head><style>h1{color:red}</style></head><body><h1>Mail body</h1><img width='100' height='100' src='cid:blue-image'>" + extra + "</body></html>";
        var mail = new StringBuilder(Headers + "Content-Type: multipart/mixed; boundary=outer\r\n\r\n--outer\r\nContent-Type: multipart/related; boundary=related\r\n\r\n--related\r\nContent-Type: text/html; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n" + Convert.ToBase64String(Encoding.UTF8.GetBytes(html)) + "\r\n");
        if (png is not null) mail.Append("--related\r\nContent-Type: image/png\r\nContent-ID: <blue-image>\r\nContent-Transfer-Encoding: base64\r\n\r\n").Append(Convert.ToBase64String(png)).Append("\r\n");
        mail.Append("--related--\r\n");
        if (attachments) for (int i = 0; i < 2; i++) mail.Append("--outer\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename*=utf-8''..%2FCON.txt\r\nContent-Transfer-Encoding: base64\r\n\r\n").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes("attachment " + i))).Append("\r\n");
        mail.Append("--outer--\r\n"); return Encoding.ASCII.GetBytes(mail.ToString());
    }
    private const string Headers = "From: =?utf-8?B?U2VuZGVyIEdyw7zDn2U=?= <sender@example.invalid>\r\nTo: Receiver <receiver@example.invalid>\r\nCc: Copy <copy@example.invalid>\r\nSubject: =?utf-8?B?UHJpdmF0ZSBHcsO8w59l?=\r\nDate: Mon, 26 Oct 2026 10:00:00 +0100\r\nMIME-Version: 1.0\r\n";
}
