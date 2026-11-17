using System;
using System.IO;
using System.Text.Json;

namespace CloudSqlProxyManager
{
    /// <summary>A glance at a Google credentials JSON file, for the hint under the field.</summary>
    public sealed class CredentialsInfo
    {
        public string Type;
        public string ClientEmail;
        public string ProjectId;
        public string Problem;

        public static CredentialsInfo Read(string path)
        {
            CredentialsInfo info = new CredentialsInfo();
            try
            {
                if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException();
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    JsonElement root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
                    JsonElement value;
                    if (root.TryGetProperty("type", out value) && value.ValueKind == JsonValueKind.String) info.Type = value.GetString();
                    if (root.TryGetProperty("client_email", out value) && value.ValueKind == JsonValueKind.String) info.ClientEmail = value.GetString();
                    if (root.TryGetProperty("project_id", out value) && value.ValueKind == JsonValueKind.String) info.ProjectId = value.GetString();
                }
                if (info.Type == null) info.Problem = "This does not look like a Google credentials file.";
            }
            catch
            {
                info.Problem = "This file could not be read as a JSON credentials file.";
            }
            return info;
        }

        public string Summary()
        {
            if (Problem != null) return Problem;
            if (Type == "service_account") return "Service account: " + (ClientEmail ?? "(no client_email)");
            return "Credential type: " + Type;
        }
    }
}
