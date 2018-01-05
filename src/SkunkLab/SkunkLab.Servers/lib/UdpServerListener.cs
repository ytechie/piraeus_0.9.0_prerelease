﻿using Piraeus.Adapters;
using Piraeus.Configuration.Settings;
using SkunkLab.Channels;
using SkunkLab.Diagnostics.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkunkLab.Security.Authentication;
using SkunkLab.Security.Tokens;

namespace SkunkLab.Servers
{
    public class UdpServerListener
    {
        public UdpServerListener(PiraeusConfig config, IPEndPoint localEP, CancellationToken token)
        {
            this.config = config;
            this.localEP = localEP;
            this.token = token;
            cache = new Dictionary<string, Tuple<ProtocolAdapter, CancellationTokenSource>>();
            container = new Dictionary<string, string>();

            SecurityTokenType stt = (SecurityTokenType)System.Enum.Parse(typeof(SecurityTokenType), config.Security.Client.TokenType, true);
            BasicAuthenticator bauthn = new BasicAuthenticator();
            bauthn.Add(stt, config.Security.Client.SymmetricKey, config.Security.Client.Issuer, config.Security.Client.Audience);
            authn = bauthn;

        }

        public event EventHandler<ServerFailedEventArgs> OnError;

        private PiraeusConfig config;
        private IAuthenticator authn;
        
        private IPEndPoint localEP;
        private CancellationToken token;
        private Dictionary<string, Tuple<ProtocolAdapter, CancellationTokenSource>> cache;
        private Dictionary<string, string> container;

        public async Task StartAsync()
        {
            UdpClient listener = new UdpClient();
            listener.ExclusiveAddressUse = false;
            listener.DontFragment = true;
            listener.Client.Bind(localEP);           
            

            Console.WriteLine("UDP listener initialized on port {0}", localEP.Port);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    UdpReceiveResult result = await listener.ReceiveAsync();
                    if (result.Buffer.Length > 0)
                    {
                        string key = String.Format("{0}:{1}", result.RemoteEndPoint.Address.ToString(), result.RemoteEndPoint.Port);

                        if (!cache.ContainsKey(key))
                        {
                            CancellationTokenSource cts = new CancellationTokenSource();
                            ProtocolAdapter adapter = ProtocolAdapterFactory.Create(config, authn, listener, result.RemoteEndPoint, cts.Token);
                            adapter.OnError += Adapter_OnError;
                            adapter.OnClose += Adapter_OnClose;
                            await adapter.Channel.OpenAsync();
                            container.Add(adapter.Channel.Id, key);
                            cache.Add(key, new Tuple<ProtocolAdapter, CancellationTokenSource>(adapter, cts));
                            adapter.Init();
                            await adapter.Channel.AddMessageAsync(result.Buffer);
                        }
                        else
                        {
                            Tuple<ProtocolAdapter, CancellationTokenSource> tuple = cache[key];
                            await tuple.Item1.Channel.AddMessageAsync(result.Buffer);
                        }
                    }
                }
                catch(Exception ex)
                {
                    OnError?.Invoke(this, new ServerFailedEventArgs("UDP", localEP.Port));
                    await Log.LogErrorAsync("UDP server channel error {0}", ex.Message);
                    
                }
            }
        }

     
        private void Sa_Completed(object sender, SocketAsyncEventArgs e)
        {
            Socket s = e.ConnectSocket;
        }





        //private async Task BindUdpClientAsync(UdpClient client)
        //{
        //    BasicAuthenticator authn = new BasicAuthenticator();
        //    CancellationTokenSource source = new CancellationTokenSource();
        //    ProtocolAdapter adapter = ProtocolAdapterFactory.Create(config, authn, client, source.Token);
        //    adapter.OnError += Adapter_OnError;
        //    adapter.OnClose += Adapter_OnClose;
        //    cache.Add(adapter.Channel.Id, new Tuple<ProtocolAdapter, CancellationTokenSource>(adapter, source));
        //    adapter.Init();
        //    await adapter.Channel.OpenAsync();
        //    await adapter.Channel.ReceiveAsync();
        //}

        private void Adapter_OnClose(object sender, ProtocolAdapterCloseEventArgs e)
        {

            if (container.ContainsKey(e.ChannelId))
            {
                if(cache.ContainsKey(container[e.ChannelId]))
                {
                    string cacheKey = container[e.ChannelId];
                    Tuple<ProtocolAdapter, CancellationTokenSource> tuple = cache[cacheKey];
                    tuple.Item2.Cancel();
                    tuple.Item1.Dispose();
                    cache.Remove(cacheKey);
                    container.Remove(e.ChannelId);
                }
            }
        }

        private void Adapter_OnError(object sender, ProtocolAdapterErrorEventArgs e)
        {
            Task task = Log.LogErrorAsync(e.Error.Message);
            Task.WhenAll(task);
        }

        public async Task StopAsync()
        {
            
            //dispose all adapters
            KeyValuePair<string, Tuple<ProtocolAdapter, CancellationTokenSource>>[] kvps = cache.ToArray();

            foreach(var kvp in kvps)
            {
                kvp.Value.Item2.Cancel();
                kvp.Value.Item1.Dispose();
            }
            
            cache.Clear();
            container.Clear();

            await TaskDone.Done;
        }
    }
}