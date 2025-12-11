# About

Language Server (LS) TCP socket adapter for LSP servers that do not offer a TCP
socket endpoint and only expect communication via stdin/stdout or a pipe.
An example of such LS is the official C# LS [Roslyn LS][roslyn-ls].

This adapter is proven to be useful, for instance, for:
Neovim LSP client on WSL2 <=> Windows LS host setup.

The reasoning for the above workflow is that one might want to do development
within a Linux context even under Windows which is why one might be motivated
to run Neovim on WSL2 while working on a project that is actually on Windows
but is mounted on WSL2 (e.g., '/mnt/c'). It is much more ideal to run the LS
natively on Windows rather than on Linux for a couple of reasons, including:

 - a project may be configured/built for Windows and the `.csproj` files contain
   Windows paths (i.e., not adjusted to the mount location of Windows under WSL).
   Even if the paths are adjusted, the LS will still not work - you are simply
   running a program under an OS (Linux) telling it to understand the structure
   of another project under a completely different OS (Windows).

 - even if this somehow works (which again, it will not) - the performance will
   be extremely bad because the LS usually has to access a lot of files (hundreds
   to thousands) and Windows file access performance via WSL2 is simply bad.

An overview of a tested use case is as follows:

```
Neovim LSP Client ----- LSP IP Socket Adapter ------- Roslyn LS
                         |            |                  |
             + - - - - - +            |                  + - - - +
             |              forward msgs from  both ends           |
     communication via      and adjust Neovim LSP client    communication via
     IP socket:             URIs to  valid Windows  URIs      stdin/stdout
     <windows-host-ip>:<port>
```

> [!Note]
> This adapter has only been tested with Roslyn LS across WSL2 Neovim client
> and Windows LS host. The code is in need of optimization (e.g., avoid string
> allocations, make use of a *global* StringBuffer, etc.).

## Installation

### Release Binaries

You can simply get the release binaries for your OS in the [releases page][relases].

### Build Instructions

In case you want to build this from the source then:

1. Install [**dotnet v >= 9.0**][dotnet]

1. In this repository's root, run:

   ```bash
   dotnet build --configuration Release
   ```

1. The LS Adapter executable is now available at: `./bin/Release/net9.0/LSPTCPSocketAdapter.exe`

## Usage

See the help message for usage details `LSPTCPSocketAdapter.exe --help`:

```
Description:
  LSP IP Socket Adapter

Usage:
  LSPTCPSocketAdapter <host> <port> <filename> <arguments> [options]

Arguments:
  <host>       IPv4 of the IP socket endpoint through which this LSP adapter communicates.
  <port>       Port of the IP socket endpoint through which this LSP adapter communicates.
  <filename>   Executable that launches the language server (e.g., dotnet) or the LS directly (passed to StartInfo.FileName).
  <arguments>  Arguments that are passed as-is to the provided filename command (passed to StartInfo.Arguments).

Options:
  --logFile <logFile>              Log file path - will be created or appended to in case it already exists. Note that LSPs usually    
                                   exchange large amount of messages, if this is provided then set the log level with disk space usage 
                                   considerations.
  --logLevel <ERROR|INFO|WARNING>  Log level of this adapter: INFO | WARNING | ERROR
  --mount <mount>                  Windows drive mount path in WSL2. If supplied, URIs and paths will be adjusted from WSL2 to Windows 
                                   by removing the leading mount path. E.g., "/mnt/c"
  -?, -h, --help                   Show help and usage information
  --version                        Show version information
```

For example, assuming you have Roslyn LS installed on Windows, run:

  ```powershell
  LSPUnixDomainSocketsAdapter.exe <ip> <port> dotnet "<roslyn-ls-path> --logLevel=Error --extensionLogDirectory=log --stdio" --mount=/mnt/c
  ```

And on WSL2, make sure that you have setup your Neovim LSP config so that it connects to the above IP and PORT, then
simply trigger the LS by opening a C# script in some valid project.

## TODOs:

- [ ] reduce (eliminate?) runtime allocations (i.e., constant number of allocations)
- [ ] add tests
- [ ] add option to capture time/performance statistics
- [ ] add limit to internal buffers resizing

## License

MIT License. See `license.txt`.

[roslyn-ls]: https://dev.azure.com/azure-public/vside/_artifacts/feed/vs-impl/NuGet/Microsoft.CodeAnalysis.LanguageServer.win-x64/overview
[dotnet]: https://learn.microsoft.com/en-us/dotnet/core/install/
[releases]: https://github.com/walcht/LSP-TCP-socket-adapter/releases
