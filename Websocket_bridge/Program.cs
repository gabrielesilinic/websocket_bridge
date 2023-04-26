using System.Net;
using WowHttp = SpaceWizards.HttpListener;
using WowWsoc = SpaceWizards.HttpListener.WebSockets;
using System.Text;
using System.IO;
using System.Text.Encodings;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Net.NetworkInformation;

class Program
{
    static int connectionCount = 0;
    static void Main(string[] args)
    {
        int firewalltries = 1;
        WowHttp.HttpListener listener = new WowHttp.HttpListener();
        string? ipenv = Environment.GetEnvironmentVariable("wsbridge_toip");
        string? wwwpath = Environment.GetEnvironmentVariable("wsbridge_www");
        var toaddresses = Dns.GetHostEntry(ipenv ?? "localhost").AddressList;
        IPEndPoint bridgeto= new IPEndPoint(toaddresses[0], 9970); ;
        int port = 9974;
        string url = "http://*:" + port + "/";
        for (int i = toaddresses.Length-1; i < toaddresses.Length + 1; i++)
        {
            int itr = (i + 1) % toaddresses.Length;
            bridgeto = new IPEndPoint(toaddresses[itr], 9970);
            if (toaddresses[itr].AddressFamily==AddressFamily.InterNetwork)
            {
                break;
            }
        }
        Task.Run(() =>
        {
            var initpos = Console.GetCursorPosition();
            int prevconcount = connectionCount;
            Console.WriteLine("Websocket connections: {0}      ", connectionCount);
            while (true)
            {
                Thread.Sleep(500);
                if (prevconcount != connectionCount)
                {
                    var currpos = Console.GetCursorPosition();
                    Console.SetCursorPosition(initpos.Left, initpos.Top);
                    Console.WriteLine("Websocket connections: {0}      ", connectionCount);
                    Console.SetCursorPosition(currpos.Left, currpos.Top);
                    prevconcount = connectionCount;
                }
            }
        });
        Thread.Sleep(500);
        //listen to your local ip as well
        //OpenFirewallTcp(port);
        while (firewalltries > 0)
        {
            try
            {
                listener = new WowHttp.HttpListener();
                listener.Prefixes.Add(url);
                listener.Start();
            }
            catch (WowHttp.HttpListenerException)
            {

                url = "http://localhost:" + port + "/";
                firewalltries++;
            }
            firewalltries--;
            if (firewalltries > 0)
            {
                Thread.Sleep(500);
            }
        }
        Console.WriteLine();
        Console.WriteLine("Connected to {0}", bridgeto);
        Console.WriteLine("Bridge available at: ");
        Console.WriteLine(url);
        while (true)
        {
            var context = listener.GetContext();
            if (context.Request.IsWebSocketRequest)
            {
                //accept the connection
                var t = context.AcceptWebSocketAsync("maps-bridge-v1");
                t.Wait();
                var socket = t.Result.WebSocket;
                if (socket is null)
                {
                    Console.WriteLine("attempt to connection via websocket failed");
                    continue;
                }
                TcpClient clientto = new TcpClient();
                try
                {
                    clientto.Connect(bridgeto);
                }
                catch (SocketException)
                {

                    socket.CloseAsync(WebSocketCloseStatus.InternalServerError, "unable to connect to server", CancellationToken.None);
                    Console.WriteLine("unable to connect to TCP server at " + bridgeto);
                    continue;
                }
                //start the bridge
                connectionCount++;
                Task.Run(() => { BridgeServer(clientto, socket); });

            }
            else
            {
                if (wwwpath is null)
                {
                    context.Response.StatusCode = 400;
                    context.Response.ContentType = "text/plain";
                    context.Response.ContentEncoding = Encoding.UTF8;
                    Console.WriteLine("err,variable wsbridge_www not set");
                    context.Response.Close();
                }
                else if (context.Request?.Url?.AbsolutePath != null)
                {
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "text/html";
                    context.Response.ContentEncoding = Encoding.UTF8;
                    string filename = context.Request.Url.AbsolutePath.Trim('/');
                    var path = Path.Combine(wwwpath, filename == "" ? "index.html" : filename);
                    //get absolute path, security measure
                    if (!Path.GetFullPath(path).ToLower().TrimEnd('/').TrimEnd('\\')
                        .Contains(wwwpath.ToLower().TrimEnd('/').TrimEnd('\\')))
                    {
                        //forbidden
                        context.Response.StatusCode = 403;
                    }
                    else if (File.Exists(path))
                    {
                        var file = File.OpenRead(path);
                        file.CopyTo(context.Response.OutputStream);
                        file.Close();
                    }
                    else
                    {
                        context.Response.StatusCode = 404;
                    }
                    context.Response.Close();
                }

            }
        }


    }
    static void BridgeServer(TcpClient from, WebSocket to)
    {
        NetworkStream tcpstream = from.GetStream();
        byte[] tcpbuffer = new byte[1024];
        ArraySegment<byte> wsbuffer = new ArraySegment<byte>(new byte[1024]);
        Task<int>? tcpwait = null;
        Task<WebSocketReceiveResult>? wswait = null;
        int checkiterator = 0;
        const int every = 6;
        bool die = false;
        while (true)
        {
            Thread.Sleep(300);
            if (checkiterator - 1 >= every)
            {
                Task checktask = to.SendAsync(UTF8Encoding.UTF8.GetBytes("\a;"), WebSocketMessageType.Text, true, CancellationToken.None);
                try
                {
                    checktask.Wait();
                }
                catch (AggregateException ex)
                {

                    ex.Handle((ex) =>
                    {
                        return ex is WebSocketException || ex is WowHttp.HttpListenerException;
                    });
                    //if it wasn't a websocket exception or an http listener exception, it woudn't reach the die
                    //therefore the program would crash anyway, for this reason putting the die bool there makes sense
                    die = true;
                }
            }
            if (die)
            {
                to.CloseAsync(WebSocketCloseStatus.Empty, "", CancellationToken.None);
                from.Close();
                break;
            }
            checkiterator += (checkiterator + 1) % every;

            string message;
            if (tcpwait is null)
            {
                tcpwait = tcpstream.ReadAsync(tcpbuffer, 0, tcpbuffer.Length);
            }
            if (wswait is null)
            {
                wswait = to.ReceiveAsync(wsbuffer, CancellationToken.None);
            }
            //forward to websocket
            if (tcpwait != null && tcpwait.IsCompletedSuccessfully && tcpwait.Result > 0)
            {
                tcpwait.Wait();
                message = Encoding.ASCII.GetString(tcpbuffer);
                message = message.Substring(0, tcpwait.Result);
                if (message != "\a;")
                    to.SendAsync(UTF8Encoding.ASCII.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);

                tcpwait = null;

            }
            //forward to tcp
            if (wswait != null && wswait.IsCompletedSuccessfully && wswait.Result.Count > 0)
            {
                wswait.Wait();
                if (wswait.Result.MessageType == WebSocketMessageType.Close)
                {
                    //close the connection
                    to.CloseAsync(WebSocketCloseStatus.NormalClosure, "connection closed", CancellationToken.None);
                    from.Close();
                    break;
                }
                if (wsbuffer.Array is not null)
                {
                    message = UTF8Encoding.ASCII.GetString(wsbuffer);
                    message = message.Substring(0, wswait.Result.Count);

                    tcpstream.WriteAsync(UTF8Encoding.ASCII.GetBytes(message), CancellationToken.None);
                }
                wswait = null;
            }
            else if (wswait != null && wswait.IsCompleted && !wswait.IsCompletedSuccessfully)
            {
                //close the connection
                to.CloseAsync(WebSocketCloseStatus.InternalServerError, "connection closed", CancellationToken.None);
                from.Close();
                break;
            }

        }
        connectionCount--;
    }

    static void OpenFirewallTcp(int port)
    {
        //uses the socket API to get firewall permission
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        socket.Listen();
        Thread.Sleep(500);
        socket.Close();
    }
}