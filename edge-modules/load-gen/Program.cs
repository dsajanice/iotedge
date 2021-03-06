﻿// Copyright (c) Microsoft. All rights reserved.

namespace LoadGen
{
    using System;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling;
    using ExponentialBackoff = Microsoft.Azure.Devices.Edge.Util.TransientFaultHandling.ExponentialBackoff;
    using Serilog;
    using Microsoft.Azure.Devices.Client.Transport.Mqtt;
    using System.Threading;
    using System.Text;
    using Newtonsoft.Json;
    using System.Security.Cryptography;
    using Microsoft.Azure.Devices.Shared;
    using Microsoft.Extensions.Logging;

    class Program
    {
        const int RetryCount = 5;
        static readonly ITransientErrorDetectionStrategy TimeoutErrorDetectionStrategy =
            new DelegateErrorDetectionStrategy(ex => ex.HasTimeoutException());
        static readonly RetryStrategy TransientRetryStrategy =
            new ExponentialBackoff(
                RetryCount,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(4));

        static long MessageIdCounter = 0;

        static async Task Main()
        {
            Microsoft.Extensions.Logging.ILogger logger = InitLogger().CreateLogger("loadgen");
            Log.Information($"Starting load run with the following settings:\r\n{Settings.Current.ToString()}");

            try
            {
                var retryPolicy = new RetryPolicy(TimeoutErrorDetectionStrategy, TransientRetryStrategy);
                retryPolicy.Retrying += (_, args) =>
                {
                    Log.Error($"Creating ModuleClient failed with exception {args.LastException}");
                    if (args.CurrentRetryCount < RetryCount)
                    {
                        Log.Information("Retrying...");
                    }
                };
                ModuleClient client = await retryPolicy.ExecuteAsync(() => InitModuleClient(Settings.Current.TransportType));

                using (var timers = new Timers())
                {
                    var random = new Random();
                    SHA256 sha = SHA256Managed.Create();
                    var bufferPool = new BufferPool();

                    // setup the message timer
                    timers.Add(
                        Settings.Current.MessageFrequency,
                        Settings.Current.JitterFactor,
                        () => GenMessage(client, random, sha, bufferPool));

                    // setup the twin update timer
                    timers.Add(
                        Settings.Current.TwinUpdateFrequency,
                        Settings.Current.JitterFactor,
                        () => GenTwinUpdate(client));
                    timers.Start();

                    (
                        CancellationTokenSource cts,
                        ManualResetEventSlim completed,
                        Option<object> handler
                    ) = ShutdownHandler.Init(TimeSpan.FromSeconds(5), logger);

                    Log.Information("Load gen running.");

                    await cts.Token.WhenCanceled();
                    Log.Information("Stopping timers.");
                    timers.Stop();
                    Log.Information("Closing connection to Edge Hub.");
                    await client.CloseAsync();
                    completed.Set();
                    handler.ForEach(h => GC.KeepAlive(h));

                    Log.Information("Load run complete. Exiting.");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error occurred during load run. \r\n{ex.ToString()}");
            }
        }

        static async void GenMessage(
            ModuleClient client,
            Random random,
            SHA256 sha,
            BufferPool bufferPool)
        {
            using (Buffer data = bufferPool.AllocBuffer(Settings.Current.MessageSizeInBytes))
            {
                // generate some bytes
                random.NextBytes(data.Data);

                // compute an SHA256 hash for this data
                byte[] hash = sha.ComputeHash(data.Data);

                // build message
                var messageBody = new
                {
                    sequenceNumber = Interlocked.Increment(ref MessageIdCounter),
                    data = data.Data,
                    hash = hash
                };

                var message = new Message(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(messageBody)));
                await client.SendEventAsync(Settings.Current.OutputName, message).ConfigureAwait(false);
            }
        }

        static async void GenTwinUpdate(ModuleClient client)
        {
            var twin = new TwinCollection();
            twin["messagesSent"] = MessageIdCounter;
            await client.UpdateReportedPropertiesAsync(twin).ConfigureAwait(false);
        }

        static ILoggerFactory InitLogger()
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            return new LoggerFactory().AddSerilog();
        }

        static async Task<ModuleClient> InitModuleClient(TransportType transportType)
        {
            ITransportSettings[] GetTransportSettings()
            {
                switch (transportType)
                {
                    case TransportType.Mqtt:
                    case TransportType.Mqtt_Tcp_Only:
                        return new ITransportSettings[] { new MqttTransportSettings(TransportType.Mqtt_Tcp_Only) };
                    case TransportType.Mqtt_WebSocket_Only:
                        return new ITransportSettings[] { new MqttTransportSettings(TransportType.Mqtt_WebSocket_Only) };
                    case TransportType.Amqp_WebSocket_Only:
                        return new ITransportSettings[] { new AmqpTransportSettings(TransportType.Amqp_WebSocket_Only) };
                    default:
                        return new ITransportSettings[] { new AmqpTransportSettings(TransportType.Amqp_Tcp_Only) };
                }
            }
            ITransportSettings[] settings = GetTransportSettings();

            ModuleClient moduleClient = await ModuleClient.CreateFromEnvironmentAsync(settings).ConfigureAwait(false);
            await moduleClient.OpenAsync().ConfigureAwait(false);

            Log.Information("Successfully initialized module client.");
            return moduleClient;
        }
    }
}
