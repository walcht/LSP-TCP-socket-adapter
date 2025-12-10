#pragma warning disable IDE0063, IDE0130
using System.CommandLine;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LSAdapter;

partial class Program
{
  public static readonly string LSP_HEADER = "Content-Length: ";

  static Task<int> Main(string[] args)
  {
    // cli arguments parsing
    var hostArg = new Argument<IPAddress>("host")
    {
      Description = "IPv4 of the IP socket endpoint through which this LSP adapter communicates.",
      CustomParser = result =>
          {
            IPAddress? ip = null;
            try
            {
              ip = IPAddress.Parse(result.Tokens[0].Value);
              if (ip == null)
                result.AddError("provived IPv4 is null");
            }
            catch (Exception e) when (e is InvalidOperationException || e is FormatException)
            {
              result.AddError("provided IP string must be a valid IPv4");
            }
            return ip;
          }
    };

    var portArg = new Argument<int>("port")
    {
      Description = "Port of the IP socket endpoint through which this LSP adapter communicates.",
      CustomParser = result =>
      {
        if (!int.TryParse(result.Tokens[0].Value, out int port))
        {
          result.AddError("port must be a valid int32");
          return 1;
        }

        if (port < 1024 || port > 65535)
        {
          result.AddError("invalid port - must be in [1024, 65535]");
          return 1;
        }

#pragma warning disable CS8604
        try
        {
          TcpListener list = new(result.GetValue(hostArg), port);
          list.Start(0);
          list.Dispose();
        }
        catch (Exception e) when (e is InvalidOperationException || e is FormatException)
        {
          return 1;
        }
        catch (SocketException)
        {
          result.AddError("invalid port - port is currently in use");
          return 1;
        }
#pragma warning restore CS8604

        return port;
      }
    };

    var filenameArg = new Argument<string>("filename")
    {
      Description = "Executable that launches the language server (e.g., dotnet) or the LS directly (passed to StartInfo.FileName).",
    };

    var lsArgs = new Argument<string>("arguments")
    {
      Description = "Arguments that are passed as-is to the provided filename command (passed to StartInfo.Arguments).",
    };

    var logFileOpt = new Option<FileInfo>("--logFile")
    {
      Description = "Log file path - will be created or appended to in case it already exists. Note that LSPs usually exchange large amount of messages, if this is provided then set the log level with disk space usage considerations."
    };

    var logLevelOpt = new Option<Logger.LogLevel?>("--logLevel")
    {
      Description = "Log level of this adapter: INFO | WARNING | ERROR ",
      CustomParser = result =>
      {
        switch (result.Tokens[0].Value.ToLower())
        {
          case "info":
            return Logger.LogLevel.INFO;
          case "warning":
            return Logger.LogLevel.WARNING;
          case "error":
            return Logger.LogLevel.ERROR;
          default:
            result.AddError("unknown provived adapter log level. Expected: INFO | WARNING | ERROR ");
            return Logger.LogLevel.ERROR;
        }
      }
    };

    var mountOpt = new Option<string>("--mount")
    {
      Description = "Windows drive mount path in WSL2. If supplied, URIs and paths will be adjusted from WSL2 to Windows by removing the leading mount path. E.g., \"/mnt/c\"",
    };

    RootCommand rootCmd = new("LSP IP Socket Adapter")
    {
      hostArg,
      portArg,
      logFileOpt,
      logLevelOpt,
      mountOpt,
      filenameArg,
      lsArgs,
    };

    rootCmd.SetAction((parseResult, ct) =>
    {
      var host = parseResult.GetValue(hostArg);
      var port = parseResult.GetValue(portArg);
      var filename = parseResult.GetValue(filenameArg);
      var _lsArgs = parseResult.GetValue(lsArgs);
      var logFilePath = parseResult.GetValue(logFileOpt);
      var logLevel = parseResult.GetValue(logLevelOpt);
      var windowsMountPathInWSL2 = parseResult.GetValue(mountOpt);
      if (windowsMountPathInWSL2 != null)
        windowsMountPathInWSL2 = windowsMountPathInWSL2.TrimEnd('/');

#pragma warning disable CS8604
      return Run(host, port, filename, _lsArgs, windowsMountPathInWSL2, logFilePath, logLevel, ct);
#pragma warning restore CS8604
    });

    return rootCmd.Parse(args).InvokeAsync();
  }

  static async Task<int> Run(IPAddress host, int port, string filename, string args,
      string? windowsMountPathInWSL2, FileInfo? logFilePath, Logger.LogLevel? logLevel, CancellationToken ct)
  {
    int exitCode = 0;

    // setup the logger
    if (logFilePath != null)
    {
      var fs = logFilePath.Open(FileMode.Append);
      var sw = new StreamWriter(fs) { AutoFlush = true };
      Logger.StreamWriter = sw;
      AppDomain.CurrentDomain.ProcessExit += (_, _) => { sw.Dispose(); fs.Dispose(); };
    }

    Logger.Level = logLevel ??= Logger.LogLevel.ERROR;

    Process p = new();

    p.StartInfo.FileName = filename;
    p.StartInfo.Arguments = args;
    p.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
    p.StartInfo.CreateNoWindow = true;
    p.StartInfo.UseShellExecute = false;
    p.StartInfo.RedirectStandardInput = true;
    p.StartInfo.RedirectStandardOutput = true;

    p.Start();

    p.StandardInput.AutoFlush = true;

    Logger.LogInfo("started LS process with PID={0} and with cmd=\"{1} {2}\"", p.Id, p.StartInfo.FileName, p.StartInfo.Arguments);

    Socket? listeningSocket = null;
    Socket? connSock = null;

    try
    {
      // create the listening TCP socket
      listeningSocket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

      // get host IP address
      IPEndPoint hostEP = new(host, port);

      // this might fail if given host or port is invalid
      listeningSocket.Bind(hostEP);
      // Logger.LogInfo($"failed to bind the listening socket to: {host}:{port}. Error code: {e.ErrorCode}");

      Logger.LogInfo("listening for client connection request on: {0}:{1} ...", host, port);

      listeningSocket.Listen(1);  // we just expect only one connection

      connSock = listeningSocket.Accept();

      listeningSocket.Close();
      listeningSocket.Dispose();
      Logger.LogInfo("accepted client connection request and closed listening socket");

      var lspClientRawBuff = new byte[262_144];
      var lspClientMsgBuff = new char[262_144];

      var lsRawBuff = new byte[262_144];
      var lsMsgBuff = new char[262_144];

      var pendingTasks = new Task[2];

      const int CLIENT_MSG_TASK = 0;
      const int LS_MSG_TASK = 1;

      pendingTasks[CLIENT_MSG_TASK] = ReadMsgFromLSPClientAsync(connSock, lspClientRawBuff, lspClientMsgBuff, 0, ct);
      pendingTasks[LS_MSG_TASK] = ReadMsgFromLS(p.StandardOutput, lsMsgBuff, ct);

      while (true)
      {
        int idx = Task.WaitAny(pendingTasks, ct);

        if (idx == CLIENT_MSG_TASK)
        {
          var res = await (Task<LSPClientResult>)pendingTasks[idx];
          pendingTasks[idx].Dispose();

          // 0 received bytes means the LSP client has closed the connection
          if (res.TotalNbrReceivedChars == 0)
          {
            Logger.LogInfo("LSP client end-of-stream reached - closing connection and killing LS process ...");
            break;
          }

          // overwrite in case buffers were resized
          lspClientRawBuff = res.RawBuff;
          lspClientMsgBuff = res.MsgBuff;
          int nbrRemainingChars = res.TotalNbrReceivedChars - (res.ContentStartIdx + res.ContentLength);

          await WriteMsgToLS(p.StandardInput, lspClientMsgBuff, res.ContentStartIdx, res.ContentLength, windowsMountPathInWSL2);

          while (nbrRemainingChars != 0)
          {
            // copy remaining chars into the start of the msg buffer
            Array.Copy(lspClientMsgBuff, res.ContentStartIdx + res.ContentLength, lspClientMsgBuff, 0, nbrRemainingChars);
            res = await ReadMsgFromLSPClientAsync(connSock, lspClientRawBuff, lspClientMsgBuff, nbrRemainingChars, ct);
            nbrRemainingChars = res.TotalNbrReceivedChars - (res.ContentStartIdx + res.ContentLength);

            await WriteMsgToLS(p.StandardInput, lspClientMsgBuff, res.ContentStartIdx, res.ContentLength, windowsMountPathInWSL2);
          }

          // and schedule the task again ...
          pendingTasks[CLIENT_MSG_TASK] = ReadMsgFromLSPClientAsync(connSock, lspClientRawBuff, lspClientMsgBuff, 0, ct);
        }
        else if (idx == LS_MSG_TASK)
        {
          var res = await (Task<LSResult>)pendingTasks[idx];
          pendingTasks[idx].Dispose();

          if (res.TotalNbrReadChars == 0)
          {
            Logger.LogInfo("LS returned end-of-stream - closing connection and killing LS process ... ");
            break;
          }

          lsMsgBuff = res.MsgBuff;

          var sendRes = await SendMsgToLSPClientAsync(connSock, lsMsgBuff, lsRawBuff, res.TotalNbrReadChars, ct);
          Logger.LogInfo("[server -> client]: \"{0}\"", new LazyLiteralStrObject(new ArraySegment<char>(lsMsgBuff, 0, res.TotalNbrReadChars)));

          // overwrite in case the byte buffer was resized
          lsRawBuff = sendRes.RawBuff;

          // LSP seems to work fine without having to adjust LS paths/URIs (maybe there aren't any?)
          // TODO: verify the comment above

          // and schedule the task again ...
          pendingTasks[LS_MSG_TASK] = ReadMsgFromLS(p.StandardOutput, lsMsgBuff, ct);
        }
        else
        {
          Logger.LogError("unexpected Task.WaitAny() index: {0}", idx);
          throw new Exception($"unexpected Task.WaitAny index: {idx}");
        }

      }  // end while

    }
    catch (SocketException e)
    {
      const int WSAECONNRESET = 10054;
      if (e.ErrorCode == WSAECONNRESET)  // LSP client exited (e.g., Neovim upon ':q<CR>')
      {
        exitCode = 0;
      }
      else
      {
        Logger.LogInfo("SocketException error code: {0}", e.ErrorCode);
        exitCode = 1;
      }
    }
    finally
    {
      // LS process cleanup
      p.Kill();
      p.Dispose();

      // listening socket cleanup
      if (listeningSocket != null)
      {
        listeningSocket.Close();
        listeningSocket.Dispose();
      }

      // connection socket cleanup
      if (connSock != null)
      {
        connSock.Shutdown(SocketShutdown.Both);
        connSock.Close();
        connSock.Dispose();
      }
    }

    if (exitCode == 0)
      Logger.LogInfo("exited gracefully");
    else
      Logger.LogError("exited due to some error(s)");

    return exitCode;
  }

  public static async Task WriteMsgToLS(StreamWriter sw, char[] msgBuff, int contentStartIdx, int contentLength, string? windowsMountPathInWSL2)
  {
    if (windowsMountPathInWSL2 != null)
    {
      string adjustedMsg = WslToWinURIsAndPaths(new ArraySegment<char>(msgBuff, contentStartIdx, contentLength), windowsMountPathInWSL2);
      await sw.WriteAsync(adjustedMsg);
      Logger.LogInfo("[client -> server]: \"{0}\"", new LazyLiteralStrObject(adjustedMsg));
    }
    else
    {
      await sw.WriteAsync(msgBuff, 0, contentStartIdx + contentLength);
      Logger.LogInfo("[client -> server]: \"{0}\"", new LazyLiteralStrObject(new ArraySegment<char>(msgBuff, 0, contentStartIdx + contentLength)));
    }
  }

  public readonly struct LSResult(int totalNbrReadChars, int contentLength, int contentStartIdx, char[] msgBuff)
  {
    public readonly int TotalNbrReadChars = totalNbrReadChars;
    public readonly int ContentLength = contentLength;
    public readonly int ContentStartIdx = contentStartIdx;
    public readonly char[] MsgBuff = msgBuff;
  }

  private static async Task<LSResult> ReadMsgFromLS(StreamReader sr, char[] msgBuff, CancellationToken ct)
  {
    int totalNbrReadChars = await sr.ReadAsync(msgBuff, ct);

    if (totalNbrReadChars == 0)
      return new LSResult(0, 0, -1, msgBuff);

    // avoid Regex because .NET's Regex.Match method does not fuckin accept anything except strings...
    GetContentLength(msgBuff, out int contentLength, out int contentStartIdx);
    if (contentLength < 0 || contentLength < 0)
    {
      string rgx = @"Content-Length: (\d+)\r\n\r\n";
      Logger.LogError("failed to match regex: {0} to received msg from LSP client: {1}", rgx, GetLiteralStr(msgBuff));
      throw new LSPMessageException($"failed to match: {rgx} - see log");
    }

    // resize char buffer so that it can fit the whole message (if needed)
    int expectedTotalSize = contentStartIdx + contentLength;
    msgBuff = ResizeBufferIfNecessary(expectedTotalSize, msgBuff, totalNbrReadChars);

    // keep reading until the whole msg is read
    while (totalNbrReadChars < expectedTotalSize)
    {
      int nbrReachChars = await sr.ReadAsync(msgBuff, totalNbrReadChars, msgBuff.Length - totalNbrReadChars);
      totalNbrReadChars += nbrReachChars;
    }
    return new LSResult(totalNbrReadChars, contentLength, contentStartIdx, msgBuff);
  }

  public readonly struct LSPClientSendResult(int totalNbrBytesSent, byte[] rawBuff)
  {
    public readonly int TotalNbrBytesSent = totalNbrBytesSent;
    public readonly byte[] RawBuff = rawBuff;
  }

  private static async Task<LSPClientSendResult> SendMsgToLSPClientAsync(Socket sock, char[] msgBuff, byte[] rawBuff, int msgCount, CancellationToken ct)
  {
    // get encoded message's number of bytes
    int totalNbrBytesToSend = Encoding.UTF8.GetByteCount(msgBuff, 0, msgCount);

    // resize the buffer that will contain the whole encoded message contained in the message buffer
    rawBuff = ResizeBufferIfNecessary(totalNbrBytesToSend, rawBuff);

    // encode message into provided bytearray
    Encoding.UTF8.GetBytes(msgBuff, 0, msgCount, rawBuff, 0);

    // keep sending until all the message's encoded bytes are sent
    int totalNbrBytesSent = 0;
    while (totalNbrBytesSent < totalNbrBytesToSend)
    {
      // ArraySegment is simply a view (same as in C++) into an array (i.e., range) - it should be very lightweight
      int nbrBytes = await sock.SendAsync(new ArraySegment<byte>(rawBuff, totalNbrBytesSent, totalNbrBytesToSend - totalNbrBytesSent), ct);
      totalNbrBytesSent += nbrBytes;
    }

    return new LSPClientSendResult(totalNbrBytesSent, rawBuff);
  }

  readonly static StringBuilder s_Int32Builder = new("2147483647".Length);

  /// gets content length and content body start index from provided message. If either is set to -1, it
  /// means that the corresponding string was not found.
  /// This does NOT use regex and only does one allocation: a string of max length == "2147483647".Length
  public static void GetContentLength(ArraySegment<char> msg, out int contentLength, out int contentStartIdx)
  {
    int i = 0;
    for (; i < LSP_HEADER.Length; ++i)
      if (msg[i] != LSP_HEADER[i])
      {
        contentLength = -1;
        contentStartIdx = -1;
        return;
      }

    // we have verified that msg starts with "Content-Length: " (case sensitive)
    int j = i;
    s_Int32Builder.Clear();
    while (char.IsAsciiDigit(msg[j]))
    {
      s_Int32Builder.Append(msg[j]);
      ++j;
    }

    // failed to find the (\d+) content length number
    if (i == j)
    {
      contentLength = -1;
      contentStartIdx = -1;
      return;
    }

    contentLength = int.Parse(s_Int32Builder.ToString());

    // check if we have the double '\r\n' sequence
    if (msg[j] != '\r' ||
        msg[j + 1] != '\n' ||
        msg[j + 2] != '\r' ||
        msg[j + 3] != '\n')
    {
      contentStartIdx = -1;
      return;
    }

    contentStartIdx = j + 4;
  }


  public readonly struct LSPClientResult(int totalNbrReceivedChars, int contentLength, int contentStartIdx, byte[] rawBuff, char[] msgBuff)
  {
    public readonly int TotalNbrReceivedChars = totalNbrReceivedChars;
    public readonly int ContentLength = contentLength;
    public readonly int ContentStartIdx = contentStartIdx;
    public readonly byte[] RawBuff = rawBuff;
    public readonly char[] MsgBuff = msgBuff;
  }

  /// the message buffer may contain more characters that what is needed for a single LSP client message. This
  /// means that the last Receive() call resulted in some residue that SHOULD be taken care of by copying the
  /// message buffer into itself at index 0 and calling this function again with msgBuffCount set to the number
  /// of residual/remaing characters.
  private static async Task<LSPClientResult> ReadMsgFromLSPClientAsync(Socket sock, byte[] rawBuff, char[] msgBuff,
      int msgBuffCount, CancellationToken ct)
  {
    int totalNbrReceivedChars = msgBuffCount;
    int nbrReceivedBytes;
    // this means there are NO unread chars in the provided msg buffer - i.e., it is simply empty
    // (that is - no residue from previous ReadMsgFromLSPClientAsync call)
    if (msgBuffCount == 0)
    {
      nbrReceivedBytes = await sock.ReceiveAsync(rawBuff, ct);
      if (nbrReceivedBytes == 0)
        return new LSPClientResult(0, 0, -1, rawBuff, msgBuff);
      totalNbrReceivedChars = Encoding.UTF8.GetCharCount(rawBuff, 0, nbrReceivedBytes);
      // resize the buffer that will contain the decoded message contained in the bytebuffer
      msgBuff = ResizeBufferIfNecessary(totalNbrReceivedChars, msgBuff);
      // decode using UTF-8 (that's the only format the LSP supports)
      Encoding.UTF8.GetChars(rawBuff, 0, nbrReceivedBytes, msgBuff, 0);
    }

    // it is assumed (as per the LSP specs) that every first ReceiveAsync call returns a msg with the header "Content-Length: (\d+)\r\n\r\n"
    // avoid Regex because .NET's Regex.Match method does not fuckin accept anything except strings...
    GetContentLength(msgBuff, out int contentLength, out int contentStartIdx);

    // it could be that we have just received a cutoff header because the reception buffer filled up
    // if that is the case then we should have some buffered data that is available for instant reading
    // (the reception buffer is always sized so that it can accomodate at least the longest possible header)
    if ((contentLength < 0 || contentLength < 0) && sock.Available > 0)
    {
      nbrReceivedBytes = await sock.ReceiveAsync(rawBuff, ct);
      int nbrReceivedChars = Encoding.UTF8.GetCharCount(rawBuff, 0, nbrReceivedBytes);
      Encoding.UTF8.GetChars(rawBuff, 0, nbrReceivedBytes, msgBuff, totalNbrReceivedChars);
      totalNbrReceivedChars += nbrReceivedChars;
      // and try again to retrieve the header
      GetContentLength(msgBuff, out contentLength, out contentStartIdx);
    }

    if (contentLength < 0 || contentLength < 0)
    {
      string rgx = @"Content-Length: (\d+)\r\n\r\n";
      Logger.LogError("failed to match regex: {0} to received msg from LSP client: {1}", rgx, GetLiteralStr(msgBuff));
      throw new LSPMessageException($"failed to match: {rgx} - see log");
    }

    // resize char buffer so that it can fit the whole message (if needed)
    int expectedTotalSize = contentStartIdx + contentLength;
    msgBuff = ResizeBufferIfNecessary(expectedTotalSize, msgBuff, totalNbrReceivedChars);

    // keep receiving from the socket until the whole message is received
    while (totalNbrReceivedChars < expectedTotalSize)
    {
      nbrReceivedBytes = await sock.ReceiveAsync(rawBuff, ct);
      int nbrReceivedChars = Encoding.UTF8.GetCharCount(rawBuff, 0, nbrReceivedBytes);

      Encoding.UTF8.GetChars(rawBuff, 0, nbrReceivedBytes, msgBuff, totalNbrReceivedChars);
      totalNbrReceivedChars += nbrReceivedChars;
    }

    // await p.StandardInput.WriteAsync(adjustedMsg);

    return new LSPClientResult(totalNbrReceivedChars, contentLength: contentLength, contentStartIdx, rawBuff, msgBuff);
  }

  private static string WslToWinURIsAndPaths(ArraySegment<char> content, string mountPath = "/mnt/c")
  {
    // e.g.:
    //  - "uri":"file:///mnt/c/Users/walid/Desktop/ctvisualizer/Assets/TestScript.cs"
    //  - "solution":"file:///mnt/c/Users/walid/Desktop/ctvisualizer/ctvisualizer.sln"
    //
    // dotnet's Regex only replaces first occurence for some fuckin reason... I am done with trying to use that shit

    string _body = new(content);
    string URI_HEADER = $"\"file://{mountPath}/";  // e.g., "file:///mnt/c/ => "file://c:/
    int startIdx = 0;
    char windowsDrive = mountPath[^1];
    // TODO: avoid creation of string buffer
    StringBuilder sb = new(_body.Length);

    // replace all URIs
    while (true)
    {
      int uriIdx = _body.IndexOf(URI_HEADER, startIdx);
      if (uriIdx < 0)
        break;

      sb.Append(_body.AsSpan(startIdx, uriIdx - startIdx));
      sb.Append($"\"file://{char.ToLower(windowsDrive)}:/");

      startIdx = uriIdx + URI_HEADER.Length;
    }
    sb.Append(_body.AsSpan(startIdx));

    // now replace all paths
    string PATH_HEADER = $"\"{mountPath}/";
    sb.Replace(PATH_HEADER, $"\"{char.ToUpper(windowsDrive)}:/");

    return $"Content-Length: {sb.Length}\r\n\r\n" + sb.ToString();
  }

  /// <summary>
  ///   Resizes the provided char buffer if its size is insufficient for the provided requested size.
  /// </summary>
  public static char[] ResizeBufferIfNecessary(int requestedSize, char[] buff, int buffCount = 0)
  {
    if (buff.Length >= requestedSize)
      return buff;

    var newSize = (int)Math.Pow(2, Math.Ceiling(Math.Log(requestedSize, 2)));
    var tmp = new char[newSize];

    // no need to copy if original buffer is 'empty'
    if (buffCount != 0)
      Array.Copy(buff, 0, tmp, 0, buffCount);

    Logger.LogInfo("increased char buffer size to: {0}", tmp.Length);
    return tmp;
  }


  /// <summary>
  ///   Resizes the provided byte buffer if its size is insufficient for the provided requested size.
  /// </summary>
  public static byte[] ResizeBufferIfNecessary(int requestedSize, byte[] buff, int buffCount = 0)
  {
    if (buff.Length >= requestedSize)
      return buff;

    var newSize = (int)Math.Pow(2, Math.Ceiling(Math.Log(requestedSize, 2)));
    var tmp = new byte[newSize];

    // no need to copy if original buffer is 'empty'
    if (buffCount != 0)
      Array.Copy(buff, 0, tmp, 0, buffCount);

    Logger.LogInfo("increased byte buffer size to: {0}", tmp.Length);
    return tmp;
  }

  public static string GetLiteralStr(IEnumerable<char> msg)
  {
    StringBuilder sb = new();

    foreach (char _char in msg)
    {
      switch (_char)
      {
        case '\t':
          sb.Append(@"\t");
          break;

        case '\r':
          sb.Append(@"\r");
          break;

        case '\n':
          sb.Append(@"\n");
          break;

        default:
          sb.Append(_char);
          break;
      }
    }

    return sb.ToString();

  }

  public class LazyLiteralStrObject(IEnumerable<char> originalStr)
  {
    private readonly IEnumerable<char> m_OriginalStr = originalStr;

    public override string ToString() => GetLiteralStr(m_OriginalStr);
  }
}

[Serializable]
public class LSPMessageException : Exception
{
  public LSPMessageException()
  {
  }

  public LSPMessageException(string? message) : base(message)
  {
  }

  public LSPMessageException(string? message, Exception? innerException) : base(message, innerException)
  {
  }
}

