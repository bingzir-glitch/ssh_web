using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Renci.SshNet.Common;
using ssh.Server.Models;
using ssh.Server.Services;

namespace ssh.Server;

public class Program
{
    private const long MaxUploadBodySize = 1024L * 1024L * 1024L;
    // WebSocket 消息统一使用 camelCase，方便前端直接按字段名读取。
    private static readonly JsonSerializerOptions WebSocketJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.WebHost.ConfigureKestrel(options =>
        {
            // 上传文件和文件夹时允许更大的请求体，避免被默认 30MB 限制拦住。
            options.Limits.MaxRequestBodySize = MaxUploadBodySize;
        });

        builder.Services.AddControllers();
        builder.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = MaxUploadBodySize;
        });
        // 这些服务会同时被 HTTP API 和 WebSocket 会话使用，注册成单例更合适。
        builder.Services.AddSingleton<SshSessionStore>();
        builder.Services.AddSingleton<SshUploadProgressStore>();
        builder.Services.AddSingleton<SshFileBrowserService>();
        builder.Services.AddSingleton<SavedSshConnectionStore>();

        var app = builder.Build();

        app.UseDefaultFiles();
        app.MapStaticAssets();

        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsJsonAsync(new { message = "服务器处理请求时出现异常，但服务仍在运行。" });
            });
        });

        app.UseWebSockets();
        app.UseAuthorization();

        app.MapControllers();
        // 前端先创建一次 SSH 会话，再拿 sessionId 升级成 WebSocket 终端连接。
        app.MapGet("/api/ssh/connections", GetSavedConnectionsAsync);
        app.MapPut("/api/ssh/connections", SaveSavedConnectionsAsync);
        app.MapPost("/api/ssh/sessions", CreateSessionAsync);
        app.MapPost("/api/ssh/files/list", ListFilesAsync);
        app.MapPost("/api/ssh/files/action", ApplyFileActionAsync);
        app.MapPost("/api/ssh/files/upload", UploadFilesAsync);
        app.MapGet("/api/ssh/files/upload-progress/{uploadId}", GetUploadProgressAsync);
        app.Map("/ws/ssh", HandleWebSocketAsync);
        app.MapFallbackToFile("/index.html");

        app.Run();
    }

    private static async Task<IResult> GetSavedConnectionsAsync(
        SavedSshConnectionStore connectionStore,
        CancellationToken cancellationToken)
    {
        var connections = await connectionStore.GetAllAsync(cancellationToken);
        return Results.Ok(connections);
    }

    private static async Task<IResult> SaveSavedConnectionsAsync(
        [FromBody] IReadOnlyList<SavedSshConnection>? connections,
        SavedSshConnectionStore connectionStore,
        CancellationToken cancellationToken)
    {
        var items = connections ?? Array.Empty<SavedSshConnection>();
        var validationErrors = ValidateSavedConnections(items);
        if (validationErrors.Count > 0)
        {
            return Results.ValidationProblem(validationErrors);
        }

        var savedConnections = await connectionStore.ReplaceAllAsync(items, cancellationToken);
        return Results.Ok(savedConnections);
    }

    private static async Task<IResult> CreateSessionAsync(
        [FromBody] SshConnectRequest request,
        SshSessionStore sessionStore,
        CancellationToken cancellationToken)
    {
        // 所有入口先做参数校验，避免无效请求直接落到 SSH 连接层。
        var validationErrors = request.Validate();
        if (validationErrors.Count > 0)
        {
            return Results.ValidationProblem(validationErrors);
        }

        try
        {
            var session = await sessionStore.CreateAsync(request, cancellationToken);

            // 这里只返回建立好的会话标识，真正的终端数据通过 WebSocket 传输。
            return Results.Ok(new
            {
                sessionId = session.Id,
                session.host,
                session.username
            });
        }
        catch (SshAuthenticationException)
        {
            return Results.BadRequest(new { message = "SSH 认证失败，请检查用户名和认证信息。" });
        }
        catch (SshConnectionException ex)
        {
            return Results.BadRequest(new { message = $"SSH 连接失败：{ex.Message}" });
        }
        catch (SshOperationTimeoutException)
        {
            return Results.BadRequest(new { message = "SSH 连接超时。" });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    private static async Task<IResult> ListFilesAsync(
        [FromBody] SshFileListRequest request,
        SshFileBrowserService fileBrowserService,
        CancellationToken cancellationToken)
    {
        var validationErrors = request.Validate();
        if (validationErrors.Count > 0)
        {
            return Results.ValidationProblem(validationErrors);
        }

        try
        {
            var result = await fileBrowserService.ListAsync(request, cancellationToken);
            return Results.Ok(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(499);
        }
        catch (SshAuthenticationException)
        {
            return Results.BadRequest(new { message = "文件列表读取失败：SSH 认证失败。" });
        }
        catch (SshConnectionException ex)
        {
            return Results.BadRequest(new { message = $"文件列表读取失败：{ex.Message}" });
        }
        catch (SshOperationTimeoutException)
        {
            return Results.BadRequest(new { message = "文件列表读取超时，程序未停止，请稍后重试。" });
        }
        catch (SftpPathNotFoundException)
        {
            return Results.BadRequest(new { message = "目标目录不存在。" });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    private static async Task<IResult> ApplyFileActionAsync(
        [FromBody] SshFileActionRequest request,
        SshFileBrowserService fileBrowserService,
        CancellationToken cancellationToken)
    {
        var validationErrors = request.Validate();
        if (validationErrors.Count > 0)
        {
            return Results.ValidationProblem(validationErrors);
        }

        try
        {
            await fileBrowserService.ApplyActionAsync(request, cancellationToken);
            return Results.Ok(new { message = "操作成功。" });
        }
        catch (SshAuthenticationException)
        {
            return Results.BadRequest(new { message = "文件操作失败：SSH 认证失败。" });
        }
        catch (SshConnectionException ex)
        {
            return Results.BadRequest(new { message = $"文件操作失败：{ex.Message}" });
        }
        catch (SshOperationTimeoutException)
        {
            return Results.BadRequest(new { message = "文件操作超时，程序未停止，请稍后重试。" });
        }
        catch (SftpPathNotFoundException)
        {
            return Results.BadRequest(new { message = "目标路径不存在。" });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    private static async Task<IResult> UploadFilesAsync(
        HttpRequest httpRequest,
        SshFileBrowserService fileBrowserService,
        CancellationToken cancellationToken)
    {
        var form = await httpRequest.ReadFormAsync(cancellationToken);
        var request = BuildUploadRequest(form);
        var validationErrors = request.Validate();
        if (validationErrors.Count > 0)
        {
            return Results.ValidationProblem(validationErrors);
        }

        try
        {
            var result = await fileBrowserService.UploadAsync(request, cancellationToken);
            return Results.Ok(result);
        }
        catch (SshAuthenticationException)
        {
            return Results.BadRequest(new { message = "文件上传失败：SSH 认证失败。" });
        }
        catch (SshConnectionException ex)
        {
            return Results.BadRequest(new { message = $"文件上传失败：{ex.Message}" });
        }
        catch (SshOperationTimeoutException)
        {
            return Results.BadRequest(new { message = "文件上传超时，程序未停止，请稍后重试。" });
        }
        catch (SftpPathNotFoundException)
        {
            return Results.BadRequest(new { message = "上传目标目录不存在。" });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    private static IResult GetUploadProgressAsync(
        string uploadId,
        SshUploadProgressStore uploadProgressStore)
    {
        return uploadProgressStore.TryGet(uploadId, out var progress)
            ? Results.Ok(progress)
            : Results.NotFound();
    }

    private static async Task HandleWebSocketAsync(HttpContext context, SshSessionStore sessionStore)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("该接口需要使用 WebSocket 请求。");
            return;
        }

        var sessionId = context.Request.Query["sessionId"].ToString();
        if (string.IsNullOrWhiteSpace(sessionId) || !sessionStore.TryTake(sessionId, out var session) || session is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("SSH 会话不存在，或已过期。");
            return;
        }

        try
        {
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var cancellationToken = linkedCts.Token;

            // Shell 输出和前端输入并行处理，任意一侧结束都进入统一收尾流程。
            var outputTask = PumpShellOutputAsync(webSocket, session, cancellationToken);
            var inputTask = PumpWebSocketInputAsync(webSocket, session, cancellationToken);

            try
            {
                await Task.WhenAny(outputTask, inputTask);
            }
            finally
            {
                linkedCts.Cancel();
                await AwaitQuietlyAsync(outputTask);
                await AwaitQuietlyAsync(inputTask);
                await CloseWebSocketQuietlyAsync(webSocket);
            }
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    private static async Task PumpShellOutputAsync(
        WebSocket webSocket,
        SshSession session,
        CancellationToken cancellationToken)
    {
        // SSH Shell 返回的是字节流，这里统一按 UTF-8 解码后再发给浏览器终端。
        var buffer = new byte[8192];
        var decoder = Encoding.UTF8.GetDecoder();
        var charBuffer = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

        while (!cancellationToken.IsCancellationRequested &&
               webSocket.State == WebSocketState.Open &&
               session.IsConnected)
        {
            var bytesRead = await session.Shell.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            if (bytesRead <= 0)
            {
                break;
            }

            var charsDecoded = decoder.GetChars(buffer, 0, bytesRead, charBuffer, 0, flush: false);
            if (charsDecoded <= 0)
            {
                continue;
            }

            var payload = new ServerWebSocketMessage("data", new string(charBuffer, 0, charsDecoded), null, null);
            await SendMessageAsync(webSocket, payload, cancellationToken);
        }

        if (webSocket.State == WebSocketState.Open)
        {
            await SendMessageAsync(
                webSocket,
                new ServerWebSocketMessage("closed", null, "SSH 会话已结束。", null),
                cancellationToken);
        }
    }

    private static async Task PumpWebSocketInputAsync(
        WebSocket webSocket,
        SshSession session,
        CancellationToken cancellationToken)
    {
        // 浏览器发来的输入、窗口尺寸变化和断开指令都从这里分发给 SSH 会话。
        var buffer = new byte[8192];

        while (!cancellationToken.IsCancellationRequested &&
               webSocket.State == WebSocketState.Open &&
               session.IsConnected)
        {
            using var messageBuffer = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await webSocket.ReceiveAsync(buffer, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                messageBuffer.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            var json = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
            var message = JsonSerializer.Deserialize<ClientWebSocketMessage>(json, WebSocketJsonOptions);

            if (message is null)
            {
                continue;
            }

            switch (message.Type?.Trim().ToLowerInvariant())
            {
                case "input" when !string.IsNullOrEmpty(message.Data):
                    // 输入保持原样写入远端 shell，避免控制字符被额外处理。
                    session.Shell.Write(message.Data);
                    session.Shell.Flush();
                    break;
                case "resize":
                    session.Resize(message.Columns, message.Rows);
                    break;
                case "disconnect":
                    return;
            }
        }
    }

    private static async Task SendMessageAsync(
        WebSocket webSocket,
        ServerWebSocketMessage message,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, WebSocketJsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await webSocket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private static SshFileUploadRequest BuildUploadRequest(IFormCollection form)
    {
        var relativePaths = form["relativePaths"];
        var uploadFiles = form.Files
            .Select((file, index) => new SshUploadFileItem
            {
                File = file,
                RelativePath = GetFormValue(relativePaths, index, file.FileName)
            })
            .ToArray();

        return new SshFileUploadRequest
        {
            UploadId = form["uploadId"].ToString(),
            Host = form["host"].ToString(),
            Port = ParsePort(form["port"]),
            Username = form["username"].ToString(),
            Password = form["password"].ToString(),
            Path = form["path"].ToString(),
            Files = uploadFiles,
            Directories = form["directories"]
                .Select(value => value?.ToString() ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray()
        };
    }

    private static string GetFormValue(StringValues values, int index, string fallback)
    {
        return index >= 0 && index < values.Count && !string.IsNullOrWhiteSpace(values[index])
            ? values[index]!
            : fallback;
    }

    private static int ParsePort(string? value)
    {
        return int.TryParse(value, out var port) ? port : 0;
    }

    private static Dictionary<string, string[]> ValidateSavedConnections(IReadOnlyList<SavedSshConnection> connections)
    {
        var errors = new Dictionary<string, string[]>();

        for (var index = 0; index < connections.Count; index++)
        {
            var itemErrors = connections[index].Validate($"connections[{index}]");
            foreach (var error in itemErrors)
            {
                errors[error.Key] = error.Value;
            }
        }

        return errors;
    }

    private static async Task AwaitQuietlyAsync(Task task)
    {
        try
        {
            await task;
        }
        // 收尾阶段只需要安静退出，取消或对象释放都不算异常场景。
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task CloseWebSocketQuietlyAsync(WebSocket webSocket)
    {
        if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "会话已关闭。",
                    CancellationToken.None);
            }
            catch (WebSocketException)
            {
            }
        }
    }

    private sealed record ClientWebSocketMessage(string? Type, string? Data, int? Columns, int? Rows);

    private sealed record ServerWebSocketMessage(string Type, string? Data, string? Message, string? Status);
}
