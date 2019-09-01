﻿// ------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using LovettSoftware.SmartSockets;
using LovettSoftware.SmartSockets.Interface;
using System.Diagnostics;

namespace LovettSoftware.SmartSockets.TestClient
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Starting Client!");

            Program p = new Program();
            p.RunTest().Wait();
        }

        string name = "client1";

        private async Task RunTest()
        {
            CancellationTokenSource source = new CancellationTokenSource();
            using (SmartSocketClient client = await SmartSocketClient.FindServerAsync("TestServer", name, new SmartSocketTypeResolver(typeof(ServerMessage), typeof(ClientMessage)), source.Token))
            {
                client.Error += OnClientError;

                for (int i = 0; i < 10; i++)
                {
                    SocketMessage response = await client.SendReceiveAsync(new ClientMessage("Howdy partner " + i, this.name, DateTime.Now));
                    ServerMessage e = (ServerMessage)response;
                    Console.WriteLine("Client Received message '{0}' from '{1}' at '{2}'", e.Id, e.Sender, e.Timestamp);
                }

                Stopwatch watch = new Stopwatch();
                watch.Start();
                for (int i = 0; i < 1000; i++)
                {
                    SocketMessage response = await client.SendReceiveAsync(new ClientMessage("test", this.name, DateTime.Now));
                    ServerMessage e = (ServerMessage)response;
                    // todo: do something with the server response.
                }
                watch.Stop();

                Console.WriteLine("Sent 1000 messages in {0} milliseconds", watch.ElapsedMilliseconds);
            }
        }

        private void OnClientError(object sender, Exception e)
        {
            var saved = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine(e.Message);
            Console.ForegroundColor = saved;
        }
    }
}
