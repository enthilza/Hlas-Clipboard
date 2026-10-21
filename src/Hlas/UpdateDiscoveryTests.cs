using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hlas;

internal static partial class UpdateTests
{
    sealed class DiscoveryHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal readonly List<string> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.Host == "api.github.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("API rate limit exceeded") });
            if (request.Headers.Authorization is not null) throw new InvalidOperationException("Anonymous discovery must not need a token.");
            return Task.FromResult(respond(request));
        }
    }

    static void VerifyDiscovery(Action<bool, string> assert)
    {
        Task.Run(async () =>
        {
            using var rsa = RSA.Create(2048);
            var manifest = new UpdateManifest("9.0.0", "v9.0.0", Updates.Runtime, "Hlas.exe", 5, new string('a', 64));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            var signature = Convert.ToBase64String(rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            var key = rsa.ToXmlString(false);
            var latest = $"https://github.com/{Updates.Repository}/releases/latest/download/update.json";
            var pinned = $"https://github.com/{Updates.Repository}/releases/download/v9.0.0/";
            HttpResponseMessage Reply(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
            using var handler = new DiscoveryHandler(request => request.RequestUri!.AbsoluteUri switch
            {
                var url when url == latest => Reply(bytes),
                var url when url == pinned + "update.sig" => Reply(Encoding.UTF8.GetBytes(signature)),
                _ => new(HttpStatusCode.NotFound)
            });
            using var client = new HttpClient(handler);
            var offer = await Updates.CheckAsync(client, key, new Version(1, 0, 0, 0), Updates.Runtime, CancellationToken.None);
            assert(offer?.Manifest == manifest && offer.DownloadUrl == pinned + "Hlas.exe" && handler.Requests.SequenceEqual(new[] { latest, pinned + "update.sig" }), "Public update discovery works without credentials when the REST API is rate-limited, with no API calls");
            assert(offer!.ManifestBytes.SequenceEqual(bytes) && offer.Signature == signature && !handler.Requests.Any(url => url.EndsWith("Hlas.exe")), "Discovery preserves the verified signed metadata and downloads no executable before an offer exists");
            // This fake server intentionally has no /latest/download/update.sig: a newly published
            // release must never pair the old manifest with the new release's signature or EXE.
            assert(handler.Requests.Last() == pinned + "update.sig" && !offer.DownloadUrl.Contains("/latest/"), "A release change during checking cannot mix the selected manifest with a newer signature or package");
            foreach (var current in new[] { new Version(9, 0, 0, 0), new Version(10, 0, 0, 0) })
                assert(await Updates.CheckAsync(client, key, current, Updates.Runtime, CancellationToken.None) is null, "A valid signed equal or older release never offers a downgrade");

            bytes = JsonSerializer.SerializeToUtf8Bytes(manifest with { Size = 6 });
            bool rejected = false;
            try { await Updates.CheckAsync(client, key, new Version(10, 0, 0, 0), Updates.Runtime, CancellationToken.None); }
            catch (InvalidDataException) { rejected = true; }
            assert(rejected, "Even an apparently older manifest must pass signature verification before reporting no update");
            var before = handler.Requests.Count;
            bytes = JsonSerializer.SerializeToUtf8Bytes(manifest with { Tag = "../../other/repository" });
            rejected = false;
            try { await Updates.CheckAsync(client, key, new Version(1, 0, 0, 0), Updates.Runtime, CancellationToken.None); }
            catch (InvalidDataException) { rejected = true; }
            assert(rejected && handler.Requests.Count == before + 1, "An untrusted malformed release tag is rejected before any further network request");

            bytes = new byte[16385]; rejected = false;
            try { await Updates.CheckAsync(client, key, new Version(1, 0, 0, 0), Updates.Runtime, CancellationToken.None); }
            catch (InvalidDataException) { rejected = true; }
            assert(rejected, "Public manifest download retains its strict size limit");
            foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests })
            {
                using var failedHandler = new DiscoveryHandler(_ => new(status));
                using var failedClient = new HttpClient(failedHandler);
                rejected = false;
                try { await Updates.CheckAsync(failedClient, key, new Version(1, 0, 0, 0), Updates.Runtime, CancellationToken.None); }
                catch (HttpRequestException ex) { rejected = ex.StatusCode == status; }
                assert(rejected && failedHandler.Requests.Count == 1, "Download-server errors propagate without claiming success or repeatedly retrying");
            }
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            before = handler.Requests.Count; bool cancelled = false;
            try { await Updates.CheckAsync(client, key, new Version(1, 0, 0, 0), Updates.Runtime, cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            assert(cancelled && handler.Requests.Count == before, "Cancelled discovery performs no network requests");
        }).GetAwaiter().GetResult();
    }
}

