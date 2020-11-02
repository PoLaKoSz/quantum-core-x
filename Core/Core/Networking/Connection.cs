using System;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using QuantumCore.Core.Constants;
using QuantumCore.Core.Packets;
using QuantumCore.Core.Utils;
using Serilog;

namespace QuantumCore.Core.Networking
{
    public class Connection
    {
        private readonly TcpClient _client;

        private BinaryWriter _writer;

        private long _lastHandshakeTime;

        public Connection(TcpClient client, Server server)
        {
            _client = client;
            Server = server;
            Id = Guid.NewGuid();
        }

        public Server Server { get; }
        public Guid Id { get; }
        public uint Handshake { get; private set; }
        public bool Handshaking { get; private set; }
        public EPhases Phase { get; private set; }

        public async void Start()
        {
            Log.Information($"New connection from {_client.Client.RemoteEndPoint}");

            var stream = _client.GetStream();
            _writer = new BinaryWriter(stream);

            StartHandshake();

            var buffer = new byte[1];

            while (true)
            {
                try
                {
                    var read = await stream.ReadAsync(buffer, 0, 1);
                    if (read != 1)
                    {
                        Log.Information("Failed to read, closing connection");
                        _client.Close();
                        break;
                    }

                    var packetDetails = Server.GetIncomingPacket(buffer[0]);
                    if (packetDetails == null)
                    {
                        Log.Information($"Received unknown header {buffer[0]:X2}");
                        _client.Close();
                        break;
                    }

                    var data = new byte[packetDetails.Size - 1];
                    read = await stream.ReadAsync(data, 0, data.Length);

                    if (read != data.Length)
                    {
                        Log.Information("Failed to read, closing connection");
                        _client.Close();
                        break;
                    }

                    var packet = Activator.CreateInstance(packetDetails.Type);
                    packetDetails.Deserialize(packet, data);
                    
                    Log.Debug($"Recv {packet}");

                    Server.CallListener(this, packet);
                }
                catch (Exception)
                {
                    Log.Information("Failed to process network data");
                    _client.Close();
                    break;
                }
            }

            Server.RemoveConnection(this);
        }

        public void Send(object packet)
        {
            // Verify that the packet is a packet and registered
            var attr = packet.GetType().GetCustomAttribute<Packet>();
            if (attr == null) throw new ArgumentException("Given packet is not a packet", nameof(packet));

            if (!Server.IsRegisteredOutgoing(packet.GetType()))
                throw new ArgumentException("Given packet is not a registered outgoing packet", nameof(packet));

            Log.Debug($"Send {packet}");
            
            // Serialize object
            var data = Server.GetOutgoingPacket(attr.Header).Serialize(packet);
            _writer.Write(data);
            _writer.Flush();
        }

        public void StartHandshake()
        {
            if (Handshaking) return;

            // Generate random handshake and start the handshaking
            Handshake = CoreRandom.GenerateUInt32();
            Handshaking = true;
            SetPhase(EPhases.Handshake);
            SendHandshake();
        }

        public bool HandleHandshake(GCHandshake handshake)
        {
            if (!Handshaking)
            {
                // We wasn't handshaking!
                Log.Information("Received handshake while not handshaking!");
                _client.Close();
                return false;
            }

            if (handshake.Handshake != Handshake)
            {
                // We received a wrong handshake
                Log.Information($"Received wrong handshake ({Handshake} != {handshake.Handshake})");
                _client.Close();
                return false;
            }

            var time = Server.ServerTime;
            var difference = time - (handshake.Time + handshake.Delta);
            if (difference >= 0 && difference <= 50)
            {
                // if we difference is less than or equal to 50ms the handshake is done and client time is synced enough
                Log.Information("Handshake done");
                Handshaking = false;

                Server.CallConnectionListener(this);
            }
            else
            {
                // calculate new delta 
                var delta = (time - handshake.Time) / 2;
                if (delta < 0)
                {
                    delta = (time - _lastHandshakeTime) / 2;
                    Log.Debug($"Delta is too low, retry with last send time");
                }

                SendHandshake((uint) time, (uint) delta);
            }

            return true;
        }

        public void SetPhase(EPhases phase)
        {
            Phase = phase;
            Send(new GCPhase
            {
                Phase = (byte) phase
            });
        }

        private void SendHandshake()
        {
            var time = Server.ServerTime;
            _lastHandshakeTime = time;
            Send(new GCHandshake
            {
                Handshake = Handshake,
                Time = (uint) time
            });
        }

        private void SendHandshake(uint time, uint delta)
        {
            _lastHandshakeTime = time;
            Send(new GCHandshake
            {
                Handshake = Handshake,
                Time = time,
                Delta = delta
            });
        }
    }
}