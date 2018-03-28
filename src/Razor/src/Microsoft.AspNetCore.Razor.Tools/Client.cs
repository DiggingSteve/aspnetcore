﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.AspNetCore.Razor.Tools
{
    internal abstract class Client : IDisposable
    {
        private static int counter;

        public abstract Stream Stream { get; }

        public abstract string Identifier { get; }

        public void Dispose()
        {
            Dispose(disposing: true);
        }

        public abstract Task WaitForDisconnectAsync(CancellationToken cancellationToken);

        protected virtual void Dispose(bool disposing)
        {
        }

        // Based on: https://github.com/dotnet/roslyn/blob/14aed138a01c448143b9acf0fe77a662e3dfe2f4/src/Compilers/Shared/BuildServerConnection.cs#L290
        public static async Task<Client> ConnectAsync(string pipeName, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            var timeoutMilliseconds = timeout == null ? Timeout.Infinite : (int)timeout.Value.TotalMilliseconds;

            try
            {
                // Machine-local named pipes are named "\\.\pipe\<pipename>".
                // We use the SHA1 of the directory the compiler exes live in as the pipe name.
                // The NamedPipeClientStream class handles the "\\.\pipe\" part for us.
                ServerLogger.Log("Attempt to open named pipe '{0}'", pipeName);

                var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                cancellationToken.ThrowIfCancellationRequested();

                ServerLogger.Log("Attempt to connect named pipe '{0}'", pipeName);
                try
                {
                    await stream.ConnectAsync(timeoutMilliseconds, cancellationToken);
                }
                catch (Exception e) when (e is IOException || e is TimeoutException)
                {
                    // Note: IOException can also indicate timeout.
                    // From docs:
                    // - TimeoutException: Could not connect to the server within the specified timeout period.
                    // - IOException: The server is connected to another client and the  time-out period has expired.
                    ServerLogger.Log($"Connecting to server timed out after {timeoutMilliseconds} ms");
                    return null;
                }

                ServerLogger.Log("Named pipe '{0}' connected", pipeName);
                cancellationToken.ThrowIfCancellationRequested();

                // The original code in Roslyn checks that the server pipe is owned by the same user for security.
                // We plan to rely on the BCL for this but it's not yet implemented:
                // See https://github.com/dotnet/corefx/issues/25427 

                return new NamedPipeClient(stream, GetNextIdentifier());
            }
            catch (Exception e) when (!(e is TaskCanceledException || e is OperationCanceledException))
            {
                ServerLogger.LogException(e, "Exception while connecting to process");
                return null;
            }
        }

        private static string GetNextIdentifier()
        {
            var id = Interlocked.Increment(ref counter);
            return "clientconnection-" + id;
        }

        private class NamedPipeClient : Client
        {
            public NamedPipeClient(NamedPipeClientStream stream, string identifier)
            {
                Stream = stream;
                Identifier = identifier;
            }

            public override Stream Stream { get; }

            public override string Identifier { get; }

            public async override Task WaitForDisconnectAsync(CancellationToken cancellationToken)
            {
                if (!(Stream is PipeStream pipeStream))
                {
                    return;
                }

                // We have to poll for disconnection by reading, PipeStream.IsConnected isn't reliable unless you
                // actually do a read - which will cause it to update its state.
                while (!cancellationToken.IsCancellationRequested && pipeStream.IsConnected)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);

                    try
                    {
                        ServerLogger.Log($"Before poking pipe {Identifier}.");
                        await Stream.ReadAsync(Array.Empty<byte>(), 0, 0, cancellationToken);
                        ServerLogger.Log($"After poking pipe {Identifier}.");
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception e)
                    {
                        // It is okay for this call to fail.  Errors will be reflected in the
                        // IsConnected property which will be read on the next iteration.
                        ServerLogger.LogException(e, $"Error poking pipe {Identifier}.");
                    }
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Stream.Dispose();
                }
            }
        }
    }
}
