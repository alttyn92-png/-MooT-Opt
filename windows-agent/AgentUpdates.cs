using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace MooT.Agent;

internal static class AgentUpdates
{
    internal record Release(string Version, long Size, string Sha256);
    internal static bool IsNewer(string candidate, string installed) =>
        System.Version.TryParse(candidate, out var next) &&
        System.Version.TryParse(installed.Split('+')[0], out var current) && next > current;

    // One check per application launch; a failed/offline check never interrupts pairing.
    internal static async Task<string?> DownloadIfAccepted(Form owner, Uri server, string? credential)
    {
        if (server.Scheme != "https") return null;
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
        Release? release;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var response = await client.GetAsync(new Uri(server, "/api/agent-release"), timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 4096) return null;
            release = JsonSerializer.Deserialize<Release>(await response.Content.ReadAsStringAsync(timeout.Token), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
        if (release == null || !IsNewer(release.Version, Application.ProductVersion) || owner.IsDisposed) return null;
        if (release.Size < 1000000 || release.Size > 200000000 || !System.Text.RegularExpressions.Regex.IsMatch(release.Sha256, "^[a-fA-F0-9]{64}$")) return null;
        if (MessageBox.Show(owner, $"Доступна версия MooT {release.Version}. Скачать обновление?\n\nУстановщик заменит старую версию. Подключение и настройки сохранятся.", "Обновление MooT", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return null;
        if (string.IsNullOrEmpty(credential))
        {
            Process.Start(new ProcessStartInfo(new Uri(server, "/optimization/").AbsoluteUri) { UseShellExecute = true });
            return null;
        }
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MooT", "updates");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "MooT-Setup-" + System.Version.Parse(release.Version) + ".exe");
        var partial = file + ".partial";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server, "/api/agent/update-download"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength != release.Size) throw new InvalidDataException();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await using (var source = await response.Content.ReadAsStreamAsync(deadline.Token))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[65536]; long total = 0; int count;
                while ((count = await source.ReadAsync(buffer, deadline.Token)) > 0)
                {
                    total += count;
                    if (total > release.Size) throw new InvalidDataException();
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                }
                if (total != release.Size) throw new InvalidDataException();
            }
            await using (var input = File.OpenRead(partial))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(input)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            File.Move(partial, file, true);
            return file;
        }
        catch
        {
            if (!owner.IsDisposed) MessageBox.Show(owner, "Обновление не скачано или не прошло проверку. Текущая версия продолжит работу. Повторите при следующем запуске.", "Обновление MooT");
            return null;
        }
        finally { try { File.Delete(partial); } catch { } }
    }
}
