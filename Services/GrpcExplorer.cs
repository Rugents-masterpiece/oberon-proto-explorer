using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Net.Client;
using Exa.OBERON.ServiceGateway.Contracts;

namespace OberonProtoExplorer.Services;

public sealed record RpcItem(string Name, string RequestType, string ResponseType);

public sealed class GrpcExplorer
{
    private readonly IConfiguration _configuration;
    private readonly Func<string, OBERONServiceGRPC.OBERONServiceGRPCClient>? _clientFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _sessionId;
    private string? _sessionKey;
    private DateTime _lastCall;
    private static readonly HashSet<string> DirectCalls = ["Ping", "GetVersion", "GetDateTime", "GetLoginSalt", "GetLoginSalts", "LoginUser", "LoginUserEx", "Authenticate", "RefreshToken", "ValidateToken", "Logout", "IsLogin", "GetSessionInfo"];

    public GrpcExplorer(IConfiguration configuration) => _configuration = configuration;

    public GrpcExplorer(IConfiguration configuration, Func<string, OBERONServiceGRPC.OBERONServiceGRPCClient> clientFactory)
        : this(configuration) => _clientFactory = clientFactory;

    public IReadOnlyList<RpcItem> Endpoints => OBERONServiceGRPC.Descriptor.Methods
        .Select(m => new RpcItem(m.Name, m.InputType.Name, m.OutputType.Name)).ToArray();

    public async Task<string> InvokeAsync(string name, string json, string? username = null, string? password = null, string? hostOverride = null, int? portOverride = null, CancellationToken cancellationToken = default)
    {
        var method = OBERONServiceGRPC.Descriptor.Methods.SingleOrDefault(x => x.Name == name) ?? throw new ArgumentException("Neznáma RPC metóda.");
        IMessage request;
        try { request = method.InputType.Parser.ParseJson(json); }
        catch (Exception ex) when (ex is InvalidJsonException or InvalidProtocolBufferException or FormatException)
        { throw new ArgumentException("Neplatná protobuf JSON požiadavka: " + ex.Message, ex); }
        var (address, effectiveUsername, effectivePassword, key) = ResolveConnection(username, password, hostOverride, portOverride, !DirectCalls.Contains(name));
        string? invokedSessionId = null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_sessionKey != key) { _sessionId = null; _sessionKey = key; }
            using var channel = GrpcChannel.ForAddress(address);
            var stub = _clientFactory?.Invoke(address) ?? new OBERONServiceGRPC.OBERONServiceGRPCClient(channel);
            if (!DirectCalls.Contains(name) && string.IsNullOrEmpty(_sessionId) && (!string.IsNullOrWhiteSpace(effectiveUsername) || !string.IsNullOrWhiteSpace(effectivePassword)))
            {
                if (string.IsNullOrWhiteSpace(effectiveUsername) || string.IsNullOrWhiteSpace(effectivePassword))
                    throw new ArgumentException("Nastavte používateľské meno aj heslo OBERON.");
                _sessionId = await LoginAsync(stub, effectiveUsername, effectivePassword, cancellationToken);
            }
            var sessionField = method.InputType.FindFieldByName("sessionId");
            var explicitSessionId = sessionField is null ? null : (string)sessionField.Accessor.GetValue(request);
            if (!string.IsNullOrEmpty(explicitSessionId) && !string.IsNullOrEmpty(_sessionId) && explicitSessionId != _sessionId)
                throw new ArgumentException("Explicitná relácia požiadavky je v konflikte s aktuálnou reláciou klienta.");
            if (sessionField is not null && !string.IsNullOrEmpty(_sessionId) && string.IsNullOrEmpty((string)sessionField.Accessor.GetValue(request)))
                sessionField.Accessor.SetValue(request, _sessionId);
            invokedSessionId = !string.IsNullOrEmpty(explicitSessionId) ? explicitSessionId : _sessionId;
            var headers = new Metadata();
            if (!string.IsNullOrEmpty(invokedSessionId)) headers.Add("userdata", invokedSessionId);
            var clientMethod = stub.GetType().GetMethods().SingleOrDefault(m => m.Name == name + "Async" && m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType == method.InputType.ClrType)
                ?? throw new InvalidOperationException("Vygenerovaná klientská metóda chýba.");
            await PauseAsync(cancellationToken);
            using var call = (IDisposable)clientMethod.Invoke(stub, new object?[] { request, headers, DateTime.UtcNow.AddSeconds(60), cancellationToken })!;
            var responseTask = (Task)call.GetType().GetProperty("ResponseAsync")!.GetValue(call)!;
            await responseTask.ConfigureAwait(false);
            var response = (IMessage)responseTask.GetType().GetProperty("Result")!.GetValue(responseTask)!;
            switch (response)
            {
                case ProxyLoginResponse login when login.Result && !string.IsNullOrEmpty(login.SessionId): _sessionId = login.SessionId; break;
                case AuthenticateResponse auth when auth.Success && !string.IsNullOrEmpty(auth.SessionId): _sessionId = auth.SessionId; break;
                case RefreshTokenResponse refresh when refresh.Success && !string.IsNullOrEmpty(refresh.SessionId): _sessionId = refresh.SessionId; break;
                case LogoutResponse logout when logout.Success && invokedSessionId == _sessionId: _sessionId = null; break;
                case IsLoginResponse isLogin when !isLogin.IsLoggedIn && invokedSessionId == _sessionId: _sessionId = null; break;
            }
            return new JsonFormatter(new JsonFormatter.Settings(formatDefaultValues: true)).Format(response);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated) { if (invokedSessionId == _sessionId) _sessionId = null; throw; }
        finally { _gate.Release(); }
    }

    // Only backend catalog discovery consumes these headers; they are never rendered in UI.
    internal async Task<Metadata> GetSessionHeadersAsync(string? username, string? password, string? host, int? port, CancellationToken ct)
    {
        var (address, user, pass, key) = ResolveConnection(username, password, host, port, true);
        await _gate.WaitAsync(ct);
        try
        {
            if (_sessionKey != key) { _sessionId = null; _sessionKey = key; }
            if (string.IsNullOrEmpty(_sessionId))
            {
                if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass)) throw new ArgumentException("Pre načítanie katalógu je potrebné prihlásenie.");
                using var channel = GrpcChannel.ForAddress(address);
                var stub = _clientFactory?.Invoke(address) ?? new OBERONServiceGRPC.OBERONServiceGRPCClient(channel);
                _sessionId = await LoginAsync(stub, user, pass, ct);
            }
            await PauseAsync(ct);
            return new Metadata { { "userdata", _sessionId } };
        }
        finally { _gate.Release(); }
    }

    private (string Address, string? Username, string? Password, string Key) ResolveConnection(string? username, string? password, string? hostOverride, int? portOverride, bool protectedCall)
    {
        var host = string.IsNullOrWhiteSpace(hostOverride) ? (_configuration["Oberon:Host"] ?? "192.168.16.169") : hostOverride;
        var port = portOverride ?? (int.TryParse(_configuration["Oberon:Port"], out var configuredPort) ? configuredPort : 21008);
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Contains('/') || port is < 1 or > 65535)
            throw new ArgumentException("Neplatný host alebo port (1–65535).");
        var address = new UriBuilder("http", host, port).Uri.AbsoluteUri;
        var manualCredentials = !string.IsNullOrWhiteSpace(username) || !string.IsNullOrWhiteSpace(password);
        if (protectedCall && manualCredentials && (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)))
            throw new ArgumentException("Pri ručnom prihlásení vyplňte meno aj heslo; údaje sa nemiešajú so serverovým .env.");
        var effectiveUsername = manualCredentials ? username : _configuration["Oberon:Username"];
        var effectivePassword = manualCredentials ? password : _configuration["Oberon:Password"];
        var configuredHost = _configuration["Oberon:Host"] ?? "192.168.16.169";
        var configuredTargetPort = int.TryParse(_configuration["Oberon:Port"], out var savedPort) ? savedPort : 21008;
        if (protectedCall && !manualCredentials && (!string.IsNullOrEmpty(effectiveUsername) || !string.IsNullOrEmpty(effectivePassword))
            && (!host.Equals(configuredHost, StringComparison.OrdinalIgnoreCase) || port != configuredTargetPort))
            throw new ArgumentException("Serverové .env prihlasovacie údaje sa používajú iba pre nakonfigurovaný cieľ. Pre iný server vyplňte vlastné meno aj heslo.");
        var credentialDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(effectivePassword ?? "")));
        return (address, effectiveUsername, effectivePassword, address + "\n" + effectiveUsername + "\n" + credentialDigest);
    }

    private async Task PauseAsync(CancellationToken ct)
    {
        var delay = _clientFactory is null ? TimeSpan.FromSeconds(5) : TimeSpan.Zero;
        var wait = _lastCall + delay - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        _lastCall = DateTime.UtcNow;
    }

    private async Task<string> LoginAsync(OBERONServiceGRPC.OBERONServiceGRPCClient stub, string username, string password, CancellationToken ct)
    {
        await PauseAsync(ct);
        using var saltsCall = stub.GetLoginSaltsAsync(new LoginSaltsRequest { Username = username }, deadline: DateTime.UtcNow.AddSeconds(60), cancellationToken: ct);
        var salts = await saltsCall.ResponseAsync;
        if (!salts.Result) throw new InvalidOperationException("GetLoginSalts failed: " + salts.Description);
        static string Hash(string salt, string pass) => Convert.ToHexString(SHA1.HashData(Encoding.GetEncoding(1250).GetBytes(salt + pass))).ToLowerInvariant();
        await PauseAsync(ct);
        using var loginCall = stub.LoginUserAsync(new LoginUserRequest {
            Username = username,
            Gen1PasswordHash = salts.Gen1Result ? Hash(salts.Gen1Salt, password) : "",
            Gen2PasswordHash = salts.Gen2Result ? Hash(salts.Gen2Salt, password) : "",
            CashRegisterGen2PasswordHash = salts.CashRegisterGen2Result ? Hash(salts.CashRegisterGen2Salt, password) : ""
        }, deadline: DateTime.UtcNow.AddSeconds(60), cancellationToken: ct);
        var login = await loginCall.ResponseAsync;
        if (!login.Result || string.IsNullOrEmpty(login.SessionId)) throw new InvalidOperationException("Login failed: " + login.Description);
        return login.SessionId;
    }
}
