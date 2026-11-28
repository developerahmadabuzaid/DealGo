using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AuctionLoadTest;

/// <summary>The outcome of one API call, timed from just before sending to the last byte read.</summary>
sealed record ApiResult<T>(int Status, T? Value, string? Code, string? Message, long StartTs, long EndTs)
{
    public bool Ok => Status is >= 200 and < 300;
    public double Ms => Clock.Ms(StartTs, EndTs);

    public T Require(string what) =>
        Ok && Value is not null ? Value : throw new InvalidOperationException($"{what} failed: {Status} {Code} {Message}");
}

/// <summary>
/// One HTTP client for every simulated user, the way one phone would use one; each request carries
/// its own user's token. No retries: the app does not retry a bid, so neither does the test.
/// </summary>
sealed class Api : IDisposable
{
    private readonly HttpClient _http;

    public Uri BaseUrl { get; }

    public Api(Uri baseUrl, TimeSpan timeout)
    {
        BaseUrl = baseUrl;
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = int.MaxValue,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };
        _http = new HttpClient(handler) { BaseAddress = baseUrl, Timeout = timeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DealGoLoadTest/1.0");
    }

    public async Task<ApiResult<T>> SendAsync<T>(
        HttpMethod method, string path, string? token, object? body = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json.Options);

        var start = Clock.Ts;
        try
        {
            using var response = await _http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            var end = Clock.Ts;
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                var value = text.Length == 0 ? default : JsonSerializer.Deserialize<T>(text, Json.Options);
                return new ApiResult<T>(status, value, null, null, start, end);
            }

            ApiError? error = null;
            try { error = JsonSerializer.Deserialize<ApiError>(text, Json.Options); } catch (JsonException) { }
            return new ApiResult<T>(status, default, error?.Code ?? $"HTTP_{status}",
                error?.Message ?? (text.Length > 200 ? text[..200] : text), start, end);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ApiResult<T>(0, default, "TIMEOUT", "No answer within the timeout.", start, Clock.Ts);
        }
        catch (HttpRequestException e)
        {
            return new ApiResult<T>(0, default, "NETWORK", e.Message, start, Clock.Ts);
        }
    }

    public Task<ApiResult<BidResultDto>> BidAsync(string token, Guid auctionId, string key) =>
        SendAsync<BidResultDto>(HttpMethod.Post, $"api/v1/auctions/{auctionId}/bids", token, new { idempotencyKey = key });

    /// <summary>Joins, accepting the auction policies the server publishes (if any), as the app does.</summary>
    public async Task<ApiResult<ParticipationDto>> JoinAsync(string token, Guid auctionId, string key)
    {
        var policy = (await SendAsync<PolicyDto>(HttpMethod.Get, "api/v1/auction-policy", null)).Value;
        return await SendAsync<ParticipationDto>(HttpMethod.Post, $"api/v1/auctions/{auctionId}/join", token,
            new { idempotencyKey = key, acceptedPolicyVersion = policy is { Required: true } ? policy.Version : (int?)null });
    }

    private sealed record PolicyDto(int Version, bool Required);

    public Task<ApiResult<SnapshotDto>> SnapshotAsync(Guid auctionId, string? token = null) =>
        SendAsync<SnapshotDto>(HttpMethod.Get, $"api/v1/auctions/{auctionId}/snapshot", token);

    public Task<ApiResult<DetailDto>> DetailAsync(Guid auctionId, string? token = null) =>
        SendAsync<DetailDto>(HttpMethod.Get, $"api/v1/auctions/{auctionId}", token);

    public Task<ApiResult<List<WinnerDto>>> WinnersAsync(Guid auctionId) =>
        SendAsync<List<WinnerDto>>(HttpMethod.Get, $"api/v1/auctions/{auctionId}/winners", null);

    public Task<ApiResult<List<LeaderboardEntry>>> LeaderboardAsync(Guid auctionId, int take) =>
        SendAsync<List<LeaderboardEntry>>(HttpMethod.Get, $"api/v1/auctions/{auctionId}/leaderboard?take={take}", null);

    public Task<ApiResult<RuntimeSnapshotDto>> DiagnosticsAsync(string adminToken) =>
        SendAsync<RuntimeSnapshotDto>(HttpMethod.Get, "api/v1/admin/diagnostics/runtime", adminToken);

    /// <summary>
    /// Server clock minus this machine's clock, from the quickest of several round trips: the server
    /// read its clock somewhere inside the round trip, most likely near the middle.
    /// </summary>
    public async Task<(TimeSpan Offset, double RttMs)> EstimateClockOffsetAsync(Guid auctionId, int samples = 12)
    {
        var best = (Offset: TimeSpan.Zero, Rtt: double.MaxValue);
        for (var i = 0; i < samples; i++)
        {
            var r = await SnapshotAsync(auctionId);
            if (!r.Ok || r.Value is null) continue;
            var middle = Clock.Utc(r.StartTs) + TimeSpan.FromMilliseconds(r.Ms / 2);
            if (r.Ms < best.Rtt) best = (r.Value.ServerTime - middle, r.Ms);
            await Task.Delay(50);
        }
        return best.Rtt == double.MaxValue ? (TimeSpan.Zero, double.NaN) : best;
    }

    public void Dispose() => _http.Dispose();
}
