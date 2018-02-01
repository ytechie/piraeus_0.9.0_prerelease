﻿using Piraeus.Core.Messaging;
using Piraeus.Core.Metadata;
using SkunkLab.Protocols.Coap;
using SkunkLab.Protocols.Mqtt;
using SkunkLab.Storage;
using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Web;


namespace Piraeus.Grains.Notifications
{
    public class AzureQueueStorageSink : EventSink
    {
        private QueueStorage storage;
        private string queue;
        private TimeSpan? ttl;
        private Uri uri;
        private Auditor auditor;

        public AzureQueueStorageSink(SubscriptionMetadata metadata)
            : base(metadata)
        {
            auditor = new Auditor();
            uri = new Uri(metadata.NotifyAddress);
            NameValueCollection nvc = HttpUtility.ParseQueryString(uri.Query);
            queue = nvc["queue"];

            string ttlString = nvc["ttl"];
            if (!String.IsNullOrEmpty(ttlString))
            {
                ttl = TimeSpan.Parse(ttlString);
            }


            Uri sasUri = null;
            Uri.TryCreate(metadata.SymmetricKey, UriKind.Absolute, out sasUri);

            if (sasUri == null)
            {
                storage = QueueStorage.New(String.Format("DefaultEndpointsProtocol=https;AccountName={0};AccountKey={1};", uri.Authority.Split(new char[] { '.' })[0], metadata.SymmetricKey), 10000, 1000);
            }
            else
            {
                string connectionString = String.Format("BlobEndpoint={0};SharedAccessSignature={1}", queue, metadata.SymmetricKey);
                storage = QueueStorage.New(connectionString,1000,5120000);
            }
        }

        public override async Task SendAsync(EventMessage message)
        {
            AuditRecord record = null;
            byte[] payload = null;

            try
            {
                payload = GetPayload(message);
                if (payload == null)
                {
                    Trace.TraceWarning("Subscription {0} could not write to queue storage sink because payload was either null or unknown protocol type.");
                    return;
                }

                await storage.EnqueueAsync(queue, payload, ttl);

                record = new AuditRecord(message.MessageId, uri.Query.Length > 0 ? uri.ToString().Replace(uri.Query, "") : uri.ToString(), "AzureQueue", "AzureQueue", payload.Length, true, DateTime.UtcNow);
            }
            catch(Exception ex)
            {
                record = new AuditRecord(message.MessageId, uri.Query.Length > 0 ? uri.ToString().Replace(uri.Query, "") : uri.ToString(), "AzureQueue", "AzureQueue", payload.Length, false, DateTime.UtcNow, ex.Message);
                throw;
            }
            finally
            {
                if(message.Audit)
                {
                    Audit(record);
                }
            }
        }


        private byte[] GetPayload(EventMessage message)
        {
            switch (message.Protocol)
            {
                case ProtocolType.COAP:
                    CoapMessage coap = CoapMessage.DecodeMessage(message.Message);
                    return coap.Payload;
                case ProtocolType.MQTT:
                    MqttMessage mqtt = MqttMessage.DecodeMessage(message.Message);
                    return mqtt.Payload;
                case ProtocolType.REST:
                    return message.Message;
                case ProtocolType.WSN:
                    return message.Message;
                default:
                    return null;
            }
        }
        private void Audit(AuditRecord record)
        {
            if (auditor.CanAudit)
            {
                Task task = auditor.WriteAuditRecordAsync(record);
                Task.WhenAll(task);
            }
        }

    }
}