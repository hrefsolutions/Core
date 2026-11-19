using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Href.Core.Tests.Integration;

[CollectionDefinition(Name)]
public sealed class MailpitCollection : ICollectionFixture<MailpitFixture>
{
    public const string Name = "Mailpit";
}

public sealed class MailpitFixture : IAsyncLifetime
{
    private const int SmtpPort = 1025;
    private const int HttpPort = 8025;

    private readonly IContainer _container = new ContainerBuilder("axllent/mailpit:v1.34")
        .WithPortBinding(SmtpPort, true)
        .WithPortBinding(HttpPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
            .ForPort(HttpPort)
            .ForPath("/readyz")))
        .Build();

    private readonly HttpClient _http = new();

    public string Host => _container.Hostname;

    public int MappedSmtpPort => _container.GetMappedPublicPort(SmtpPort);

    private Uri ApiBase => new($"http://{_container.Hostname}:{_container.GetMappedPublicPort(HttpPort)}");

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _container.DisposeAsync();
    }

    /// <summary>
    /// Finds messages by subject through the Mailpit search API and returns each one as the JSON of
    /// <c>GET /api/v1/message/{ID}</c> (fields <c>From</c>, <c>To</c>, <c>Subject</c>, <c>HTML</c>, <c>Text</c>).
    /// </summary>
    public async Task<IReadOnlyList<JsonElement>> FindMessagesBySubjectAsync(string subject, CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString($"subject:\"{subject}\"");
        using var searchResponse = await _http.GetAsync(new Uri(ApiBase, $"/api/v1/search?query={query}"), cancellationToken);
        searchResponse.EnsureSuccessStatusCode();
        using var search = JsonDocument.Parse(await searchResponse.Content.ReadAsStringAsync(cancellationToken));

        var messages = new List<JsonElement>();

        foreach (var summary in search.RootElement.GetProperty("messages").EnumerateArray())
        {
            var id = summary.GetProperty("ID").GetString();
            using var messageResponse = await _http.GetAsync(new Uri(ApiBase, $"/api/v1/message/{id}"), cancellationToken);
            messageResponse.EnsureSuccessStatusCode();
            using var message = JsonDocument.Parse(await messageResponse.Content.ReadAsStringAsync(cancellationToken));
            messages.Add(message.RootElement.Clone());
        }

        return messages;
    }
}
