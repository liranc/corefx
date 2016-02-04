// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Sockets
{
    partial class TcpClient
    {
        // The Unix implementation is separate from the Windows implementation due to a fundamental
        // difference in socket support on the two platforms: on Unix once a socket has been used
        // in a failed connect, its state is undefined and it can no longer be used (Windows supports
        // this).  This then means that the instance Socket.Connect methods that take either multiple
        // addresses or a string host name that could be mapped to multiple addresses via DNS don't
        // work, because we can't try to individually connect to a second address after failing the
        // first.  That then impacts TcpClient, which exposes not only such Connect methods that
        // just delegate to the underlying Socket, but also the Socket itself.
        //
        // To address the most common cases, where the Socket isn't actually accessed directly by
        // a consumer of TcpClient, whereas on Windows TcpClient creates the socket during construction,
        // on Unix we create the socket on-demand, either when it's explicitly asked for via the Client
        // property or when the TcpClient.Connect methods are called.  In the former case, we have no
        // choice but to create a Socket, after which point we'll throw PlatformNotSupportedException
        // on any subsequent attempts to use Connect with multiple addresses.  In the latter case, though,
        // we can create a new Socket for each address we try, and only store as the "real" Client socket
        // the one that successfully connected.
        //
        // However, TcpClient also exposes some properties for common configuration of the Socket, and
        // if we simply forwarded those through Client, we'd frequently end up forcing the Socket's
        // creation before Connect is used, thwarting this whole scheme.  To address that, we maintain
        // shadow values for the relevant state.  When the properties are read, we read them initially
        // from a temporary socket created just for the purpose of getting the system's default. When
        // the properties are written, we store their values into these shadow properties instead of into
        // an actual socket (though we do also create a temporary socket and store the property onto it,
        // enabling errors to be caught earlier).  This is a relatively expensive, but it enables
        // TcpClient to be used in its recommended fashion and with its most popular APIs.

        // Shadow values for storing data set through TcpClient's properties.
        // We use a separate bool field to store whether the value has been set.
        // We don't use nullables, due to one of the properties being a refernece type.

        private int _exclusiveAddressUse;
        private bool _exclusiveAddressUseInitialized;

        private int _receiveBufferSize;
        private bool _receiveBufferSizeInitialized;

        private int _sendBufferSize;
        private bool _sendBufferSizeInitialized;

        private int _receiveTimeout;
        private bool _receiveTimeoutInitialized;

        private int _sendTimeout;
        private bool _sendTimeoutInitialized;

        private LingerOption _lingerState;
        private bool _lingerStateInitialized;

        private int _noDelay;
        private bool _noDelayInitialized;

        private int _connectRunning; // tracks whether a connect operation that could set _clientSocket is currently running

        private void InitializeClientSocket()
        {
            // Nop.  We want to lazily-allocate the socket.
        }

        private Socket ClientCore
        {
            get
            {
                EnterClientLock();
                try
                {
                    // The Client socket is being explicitly accessed, so we're forced
                    // to create it if it doesn't exist.
                    if (_clientSocket == null)
                    {
                        // Create the socket, and transfer to it any of our shadow properties.
                        _clientSocket = CreateSocket();
                        TransferInitializedValuesToSocket(_clientSocket);
                    }
                    return _clientSocket;
                }
                finally
                {
                    ExitClientLock();
                }
            }
            set
            {
                EnterClientLock();
                try
                {
                    _clientSocket = value;
                    ClearInitializedValues();
                }
                finally
                {
                    ExitClientLock();
                }
            }
        }

        private void TransferInitializedValuesToSocket(Socket socket)
        {
            // For each shadow property where we have an initialized value,
            // transfer that value to the provided socket.

            if (_exclusiveAddressUseInitialized)
            {
                socket.ExclusiveAddressUse = _exclusiveAddressUse != 0;
            }

            if (_receiveBufferSizeInitialized)
            {
                socket.ReceiveBufferSize = _receiveBufferSize;
            }

            if (_sendBufferSizeInitialized)
            {
                socket.SendBufferSize = _sendBufferSize;
            }

            if (_receiveTimeoutInitialized)
            {
                socket.ReceiveTimeout = _receiveTimeout;
            }

            if (_sendTimeoutInitialized)
            {
                socket.SendTimeout = _sendTimeout;
            }

            if (_lingerStateInitialized)
            {
                socket.LingerState = _lingerState;
            }

            if (_noDelayInitialized)
            {
                socket.NoDelay = _noDelay != 0;
            }
        }

        private void ClearInitializedValues()
        {
            // Clear the initialized fields for all of our shadow properties.
            _exclusiveAddressUseInitialized =
                _receiveBufferSizeInitialized =
                _sendBufferSizeInitialized =
                _receiveTimeoutInitialized =
                _sendTimeoutInitialized =
                _lingerStateInitialized =
                _noDelayInitialized =
                false;
        }

        private int AvailableCore
        {
            get
            {
                // If we have a client socket, return its available value.
                // Otherwise, there isn't data available, so return 0.
                return _clientSocket != null ? _clientSocket.Available : 0;
            }
        }

        private bool ConnectedCore
        {
            get
            {
                // If we have a client socket, return whether it's connected.
                // Otherwise as we don't have a socket, by definition it's not.
                return _clientSocket != null ? _clientSocket.Connected : false;
            }
        }

        private Task ConnectAsyncCore(string host, int port)
        {
            // Validate the args, similar to how Socket.Connect(string, int) would.
            if (host == null)
            {
                throw new ArgumentNullException("host");
            }
            if (!TcpValidationHelpers.ValidatePortNumber(port))
            {
                throw new ArgumentOutOfRangeException("port");
            }

            // If the client socket has already materialized, this API can't be used.
            if (_clientSocket != null)
            {
                throw new PlatformNotSupportedException(SR.net_sockets_connect_multiaddress_notsupported);
            }

            // Do the real work.
            EnterClientLock();
            return ConnectAsyncCorePrivate(host, port);
        }

        private async Task ConnectAsyncCorePrivate(string host, int port)
        {
            try
            {
                // Since Socket.Connect(host, port) won't work, get the addresses manually,
                // and then delegate to Connect(IPAddress[], int).
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
                await ConnectAsyncCorePrivate(addresses, port).ConfigureAwait(false);
            }
            finally
            {
                ExitClientLock();
            }
        }

        private Task ConnectAsyncCore(IPAddress[] addresses, int port)
        {
            // Validate the args, similar to how Socket.Connect(IPAddress[], int) would.
            if (addresses == null)
            {
                throw new ArgumentNullException("addresses");
            }
            if (addresses.Length == 0)
            {
                throw new ArgumentException(SR.net_invalidAddressList, "addresses");
            }
            if (!TcpValidationHelpers.ValidatePortNumber(port))
            {
                throw new ArgumentOutOfRangeException("port");
            }

            // If the client socket has already materialized, this API can't be used.
            // Similarly if another operation to set the socket is already in progress.
            if (_clientSocket != null)
            {
                throw new PlatformNotSupportedException(SR.net_sockets_connect_multiaddress_notsupported);
            }

            // Do the real work.
            EnterClientLock();
            return ConnectAsyncCorePrivate(addresses, port);
        }

        private async Task ConnectAsyncCorePrivate(IPAddress[] addresses, int port)
        {
            try
            {
                // For each address, create a new socket (configured appropriately) and try to connect
                // to the endpoint.  If we're successful, set the newly connected socket as the client
                // socket, and we're done.  If we're unsuccessful, try the next address.
                ExceptionDispatchInfo lastException = null;
                foreach (IPAddress address in addresses)
                {
                    try
                    {
                        Socket s = CreateSocket();
                        TransferInitializedValuesToSocket(s);
                        await s.ConnectAsync(address, port).ConfigureAwait(false);

                        _clientSocket = s;
                        _active = true;

                        return;
                    }
                    catch (Exception exc)
                    {
                        lastException = ExceptionDispatchInfo.Capture(exc);
                    }
                }

                // None of the addresses worked.  Throw whatever exception we last captured, or a
                // new one if something went terribly wrong.

                if (lastException != null)
                {
                    lastException.Throw();
                }

                throw new ArgumentException(SR.net_invalidAddressList, "addresses");
            }
            finally
            {
                ExitClientLock();
            }
        }

        private int ReceiveBufferSizeCore
        {
            get { return GetOption(ref _receiveBufferSize, ref _receiveBufferSizeInitialized, SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer); }
            set { SetOption(ref _receiveBufferSize, ref _receiveBufferSizeInitialized, SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, value); }
        }

        private int SendBufferSizeCore
        {
            get { return GetOption(ref _sendBufferSize, ref _sendBufferSizeInitialized, SocketOptionLevel.Socket, SocketOptionName.SendBuffer); }
            set { SetOption(ref _sendBufferSize, ref _sendBufferSizeInitialized, SocketOptionLevel.Socket, SocketOptionName.SendBuffer, value); }
        }

        private int ReceiveTimeoutCore
        {
            get { return GetOption(ref _receiveTimeout, ref _receiveTimeoutInitialized, SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout); }
            set { SetOption(ref _receiveTimeout, ref _receiveTimeoutInitialized, SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, value); }
        }

        private int SendTimeoutCore
        {
            get { return GetOption(ref _sendTimeout, ref _sendTimeoutInitialized, SocketOptionLevel.Socket, SocketOptionName.SendTimeout); }
            set { SetOption(ref _sendTimeout, ref _sendTimeoutInitialized, SocketOptionLevel.Socket, SocketOptionName.SendTimeout, value); }
        }

        private LingerOption LingerStateCore
        {
            get { return GetOption(ref _lingerState, ref _lingerStateInitialized, SocketOptionLevel.Socket, SocketOptionName.Linger); }
            set { SetOption(ref _lingerState, ref _lingerStateInitialized, SocketOptionLevel.Socket, SocketOptionName.Linger, value); }
        }

        private bool NoDelayCore
        {
            get { return GetOption(ref _noDelay, ref _noDelayInitialized, SocketOptionLevel.Tcp, SocketOptionName.NoDelay) != 0; }
            set { SetOption(ref _noDelay, ref _noDelayInitialized, SocketOptionLevel.Tcp, SocketOptionName.NoDelay, value ? 1 : 0); }
        }

        private bool ExclusiveAddressUseCore
        {
            get
            {
                return GetOption(ref _exclusiveAddressUse, ref _exclusiveAddressUseInitialized, SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse) != 0;
            }
            set
            {
                // Unlike the rest of the properties, we code this one explicitly, so as to take advantage of validation done by Socket's property.
                _exclusiveAddressUse = value ? 1 : 0;
                _exclusiveAddressUseInitialized = true;
                if (_clientSocket != null)
                {
                    _clientSocket.ExclusiveAddressUse = value; // Use setter explicitly as it does additional validation beyond that done by SetOption
                }
            }
        }

        private T GetOption<T>(ref T location, ref bool initialized, SocketOptionLevel level, SocketOptionName name)
        {
            // Used in the getter for each shadow property.  

            // If we already have our client socket set up, just get its value for the option.  
            // Otherwise, return our shadow property.  If that shadow hasn't yet been initialized, 
            // first initialize it by creating a temporary socket from which we can obtain a default value.

            if (_clientSocket != null)
            {
                return (T)_clientSocket.GetSocketOption(level, name);
            }

            if (!initialized)
            {
                using (Socket s = CreateSocket())
                {
                    location = (T)s.GetSocketOption(level, name);
                }
                initialized = true;
            }

            return location;
        }

        private void SetOption<T>(ref T location, ref bool initialized, SocketOptionLevel level, SocketOptionName name, T value)
        {
            // Used in the setter for each shadow property.

            // Store the value into our shadow
            location = value;
            initialized = true;

            // Then store the option on to the client socket.  If the client socket isn't yet set, we still want to set
            // the property onto a socket so we can get validation performed by the underlying system on the option being
            // set... so, albeit being a bit expensive, we create a temporary socket we can use for validation purposes.
            Socket s = _clientSocket ?? CreateSocket();
            try
            {
                if (typeof(T) == typeof(int))
                {
                    s.SetSocketOption(level, name, (int)(object)value);
                }
                else
                {
                    Debug.Assert(typeof(T) == typeof(LingerOption));
                    s.SetSocketOption(level, name, value);
                }
            }
            finally
            {
                if (s != _clientSocket)
                {
                    s.Dispose();
                }
            }
        }

        private void EnterClientLock()
        {
            // TcpClient is not safe to be used concurrently.  But in case someone does access various members
            // while an async Connect operation is running that could asynchronously transition _clientSocket
            // from null to non-null, we have a simple lock... if it's taken when you try to take it, it throws
            // a PlatformNotSupportedException indicating the limitations of Connect.
            if (Interlocked.CompareExchange(ref _connectRunning, 1, 0) != 0)
            {
                throw new PlatformNotSupportedException(SR.net_sockets_connect_multiaddress_notsupported);
            }
        }

        private void ExitClientLock()
        {
            Volatile.Write(ref _connectRunning, 0);
        }

    }
}
