using System;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text;
using System.Security.Cryptography;
using System.Security.Policy;

namespace SOCKS4_proxy_server_Allayarov
{

    internal class Program
    {
        public enum SOCKS4_VER : byte { REQUEST = 0x04, REPLY = 0x00 }
        public enum SOCKS4_CMD : byte { CONNECT = 0x01, BIND = 0x02 }
        public enum SOCKS4_REPLY : byte
        {
            GRANTED = 0x5A,
            FAILED_OR_REJECTED = 0x5B,
            //FAILED_NO_IDENTD = 0x5C,
            //FAILED_BAD_IDENTD = 0x5D
        }

        private const int BufferSize = 4096;
        private const int DefaultPort = 1080;
        
        public static  byte[] ReceiveSOCKSRequest(NetworkStream stream)
        {
            byte[] request = new byte[8];
            stream.Read(request, 0, 8);

            byte version = request[0];
            byte command = request[1];

            if (version != (byte)SOCKS4_VER.REQUEST)
            {
                Console.WriteLine($"Использована иная или не существующая версия SOCKS: {version}");
                stream.Dispose();
                stream.Close();
            }

            if (command != (byte)SOCKS4_CMD.CONNECT && command != (byte)SOCKS4_CMD.BIND)
            {
                Console.WriteLine($"Использована не существующая команда: {command}");
                stream.Dispose();
                stream.Close();
            }
            Console.WriteLine($"[{DateTime.Now.ToString("hh:mm:ss:fff")}] SOCKS запрос: {version}; {command}; {request[2] << 8 | request[3]}; {request[4]}.{request[5]}.{request[6]}.{request[7]}");
            return request;
        }
        public static string ReceiveNullTerminatedString(NetworkStream stream)
        {
            List<byte> nullTerminatedString = new List<byte>();
            while (true)
            {
                int oneByte = stream.ReadByte();
                if (oneByte == 0) break;
                nullTerminatedString.Add((byte)oneByte);
            }
            return Encoding.UTF8.GetString(nullTerminatedString.ToArray());
        }

        public static async Task SendSOCKSResponse(NetworkStream stream, byte status, IPAddress ip, int port)
        {
            byte[] response = new byte[8];

            response[0] = (byte)SOCKS4_VER.REPLY;
            response[1] = status;
            response[2] = (byte)(port >> 8);
            response[3] = (byte)(port & 0xFF);
            Array.Copy(ip.GetAddressBytes(), 0, response, 4, 4);
            await stream.WriteAsync(response, 0, response.Length);

            Console.WriteLine($"[{DateTime.Now.ToString("hh:mm:ss:fff")}] SOCKS ответ: {response[0]}; {status}; {port}; {ip.ToString()}");
        }

        public static async Task TransmitData(NetworkStream clientStream, NetworkStream dstStream)
        {
            Task clientToDst = Task.Run(async () =>
            {
                byte[] buffer = new byte[BufferSize];
                int bytesRead;
                while ((bytesRead = await clientStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    dstStream.Write(buffer, 0, bytesRead);
                }
            });

            Task dstToClient = Task.Run(async () =>
            {
                byte[] buffer = new byte[BufferSize];
                int bytesRead;
                while ((bytesRead = await dstStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    clientStream.Write(buffer, 0, bytesRead);
                }
            });

            await Task.WhenAll(clientToDst, dstToClient);
        }

        public static async Task ConnectAndTransmit(NetworkStream clientStream, IPAddress dstIP, int dstPort)
        {
            using (TcpClient dstClient = new TcpClient())
            {
                dstClient.Connect(dstIP, dstPort);

                if (dstClient.Connected)
                {
                    await SendSOCKSResponse(clientStream, (byte)SOCKS4_REPLY.GRANTED, dstIP, dstPort);
                    await TransmitData(clientStream, dstClient.GetStream());
                }
                else
                {
                    await SendSOCKSResponse(clientStream, (byte)SOCKS4_REPLY.FAILED_OR_REJECTED, dstIP, dstPort);
                    clientStream.Dispose();
                    clientStream.Close();
                    Console.WriteLine($"[{DateTime.Now.ToString("hh:mm:ss:fff")}] Подключение отклонено");
                }
            }
        }

        public static async Task SOCKS4Proxy(TcpClient client)
        {
            using(client)
            using(NetworkStream clientStream = client.GetStream())
            {
                byte[] socksRequest = ReceiveSOCKSRequest(clientStream);
                int dstPort = socksRequest[2] << 8 | socksRequest[3];
                IPAddress dstIP = IPAddress.Parse($"{socksRequest[4]}.{socksRequest[5]}.{socksRequest[6]}.{socksRequest[7]}");

                string userID = ReceiveNullTerminatedString(clientStream);

                // Обработка SOCKS4A (если IP = 0.0.0.x)
                if (socksRequest[4] == 0 && socksRequest[5] == 0 && socksRequest[6] == 0 && socksRequest[7] != 0)
                {
                    string domainName = ReceiveNullTerminatedString(clientStream);
                    dstIP = Dns.GetHostAddresses(domainName)[0];
                }

                if (socksRequest[1] == (byte)SOCKS4_CMD.CONNECT)
                {
                    Console.WriteLine($"[{DateTime.Now.ToString("hh:mm:ss:fff")}] Передача данных {client.Client.RemoteEndPoint} <--> {dstIP.ToString()}:{dstPort}");
                    await ConnectAndTransmit(clientStream, dstIP, dstPort);
                    Console.WriteLine($"[{DateTime.Now.ToString("hh:mm:ss:fff")}] Передача данных {client.Client.RemoteEndPoint} <--> {dstIP.ToString()}:{dstPort} окончена. Клиент ");
                }
                else if (socksRequest[1] == (byte)SOCKS4_CMD.BIND)
                {
                    TcpListener bindListener = new TcpListener(IPAddress.Any, 0);
                    bindListener.Start(1);
                    IPAddress bindIP = ((IPEndPoint)bindListener.LocalEndpoint).Address;
                    int bindPort = ((IPEndPoint)bindListener.LocalEndpoint).Port;
                    await SendSOCKSResponse(clientStream, (byte)SOCKS4_REPLY.GRANTED, bindIP, bindPort);
                    TcpClient dstClient = await bindListener.AcceptTcpClientAsync();
                    if (dstClient.Connected)
                    {
                        await SendSOCKSResponse(clientStream, (byte)SOCKS4_REPLY.GRANTED, dstIP, dstPort);
                        await TransmitData(clientStream, dstClient.GetStream());
                    }
                    else
                    {
                        await SendSOCKSResponse(clientStream, (byte)SOCKS4_REPLY.FAILED_OR_REJECTED, dstIP, dstPort);
                        clientStream.Dispose();
                        clientStream.Close();
                        Console.WriteLine($"[{DateTime.Now.ToString("hh:mm:ss:fff")}] Подключение отклонено");
                    }
                }
            }
        }

        static async Task Main(string[] args)
        {
            IPAddress proxyIP = IPAddress.Loopback;
            int proxyPort = DefaultPort;
            for (int i=0; i<args.Length; i++)
            {
                if (args[i] == "/ip")
                    IPAddress.TryParse(args[i+1], out proxyIP);
                if (args[i] == "/port")
                    proxyPort = Convert.ToInt32(args[i + 1]);
            }
            TcpListener proxy = new TcpListener(proxyIP, proxyPort);
            proxy.Start();
            Console.WriteLine($"[{DateTime.Now.ToString("hh:mm:ss:fff")}] Прокси сервер запущен на {proxy.LocalEndpoint}");

            while (true)
            {
                TcpClient client = await proxy.AcceptTcpClientAsync();
                Task.Run(()=> SOCKS4Proxy(client));
            }

        }
    }   
}
