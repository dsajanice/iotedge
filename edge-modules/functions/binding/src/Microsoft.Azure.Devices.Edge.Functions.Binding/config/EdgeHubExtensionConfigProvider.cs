// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.Azure.Devices.Edge.Functions.Binding.Config
{
    using System;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Devices.Edge.Functions.Binding.Bindings;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Azure.WebJobs.Host;
    using Microsoft.Azure.WebJobs.Host.Config;
    using Microsoft.Azure.WebJobs.Host.Triggers;
    using Newtonsoft.Json;

    /// <summary>
    /// Extension configuration provider used to register EdgeHub triggers and binders
    /// </summary>
    public class EdgeHubExtensionConfigProvider : IExtensionConfigProvider
    {
        public void Initialize(ExtensionConfigContext context)
        {
            const string ClientTransportType = "ClientTransportType";

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var extensions = context.Config.GetService<IExtensionRegistry>();
            var nameResolver = context.Config.GetService<INameResolver>();

            TransportType transportType = Utils.ToTransportType(
                nameResolver.Resolve(ClientTransportType), TransportType.Mqtt_Tcp_Only);

            // register trigger binding provider
            var triggerBindingProvider = new EdgeHubTriggerBindingProvider(transportType);
            extensions.RegisterExtension<ITriggerBindingProvider>(triggerBindingProvider);

            extensions.RegisterBindingRules<EdgeHubAttribute>();
            FluentBindingRule<EdgeHubAttribute> rule = context.AddBindingRule<EdgeHubAttribute>();
            rule.BindToCollector<Message>(typeof(EdgeHubCollectorBuilder), transportType);

            context.AddConverter<Message, string>(this.MessageConverter);
            context.AddConverter<string, Message>(this.ConvertToMessage);
        }

        Message ConvertToMessage(string str)
        {
            return JsonConvert.DeserializeObject<Message>(str);
        }

        string MessageConverter(Message msg)
        {
            return JsonConvert.SerializeObject(msg);
        }
    }
}
