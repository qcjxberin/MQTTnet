﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet.Core.Adapter;
using MQTTnet.Core.Diagnostics;
using MQTTnet.Core.Exceptions;
using MQTTnet.Core.Internal;
using MQTTnet.Core.Packets;
using MQTTnet.Core.Protocol;

namespace MQTTnet.Core.Client
{
    public class MqttClient
    {
        private readonly ConcurrentDictionary<ushort, MqttPublishPacket> _pendingExactlyOncePublishPackets = new ConcurrentDictionary<ushort, MqttPublishPacket>();
        private readonly HashSet<ushort> _processedPublishPackets = new HashSet<ushort>();

        private readonly MqttPacketDispatcher _packetDispatcher = new MqttPacketDispatcher();
        private readonly MqttClientOptions _options;
        private readonly IMqttCommunicationAdapter _adapter;

        private int _latestPacketIdentifier;
        private CancellationTokenSource _cancellationTokenSource;

        public MqttClient(MqttClientOptions options, IMqttCommunicationAdapter adapter)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        }

        public event EventHandler Connected;

        public event EventHandler Disconnected;

        public event EventHandler<MqttApplicationMessageReceivedEventArgs> ApplicationMessageReceived;

        public bool IsConnected { get; private set; }

        public async Task ConnectAsync(MqttApplicationMessage willApplicationMessage = null)
        {
            MqttTrace.Verbose(nameof(MqttClient), "Trying to connect.");

            if (IsConnected)
            {
                throw new MqttProtocolViolationException("It is not allowed to connect with a server after the connection is established.");
            }

            var connectPacket = new MqttConnectPacket
            {
                ClientId = _options.ClientId,
                Username = _options.UserName,
                Password = _options.Password,
                KeepAlivePeriod = (ushort)_options.KeepAlivePeriod.TotalSeconds,
                WillMessage = willApplicationMessage
            };

            await _adapter.ConnectAsync(_options, _options.DefaultCommunicationTimeout);
            MqttTrace.Verbose(nameof(MqttClient), "Connection with server established.");

            _cancellationTokenSource = new CancellationTokenSource();
            _latestPacketIdentifier = 0;
            _processedPublishPackets.Clear();
            _packetDispatcher.Reset();
            IsConnected = true;

            Task.Run(async () => await ReceivePackets(_cancellationTokenSource.Token), _cancellationTokenSource.Token).Forget();

            var response = await SendAndReceiveAsync<MqttConnAckPacket>(connectPacket);
            if (response.ConnectReturnCode != MqttConnectReturnCode.ConnectionAccepted)
            {
                throw new MqttConnectingFailedException(response.ConnectReturnCode);
            }

            if (_options.KeepAlivePeriod != TimeSpan.Zero)
            {
                Task.Run(async () => await SendKeepAliveMessagesAsync(_cancellationTokenSource.Token), _cancellationTokenSource.Token).Forget();
            }

            Connected?.Invoke(this, EventArgs.Empty);
        }

        public async Task DisconnectAsync()
        {
            await SendAsync(new MqttDisconnectPacket());
            await DisconnectInternalAsync();
        }

        public async Task<IList<MqttSubscribeResult>> SubscribeAsync(params TopicFilter[] topicFilters)
        {
            if (topicFilters == null) throw new ArgumentNullException(nameof(topicFilters));

            return await SubscribeAsync(topicFilters.ToList());
        }

        public async Task<IList<MqttSubscribeResult>> SubscribeAsync(IList<TopicFilter> topicFilters)
        {
            if (topicFilters == null) throw new ArgumentNullException(nameof(topicFilters));
            if (!topicFilters.Any()) throw new MqttProtocolViolationException("At least one topic filter must be set [MQTT-3.8.3-3].");
            ThrowIfNotConnected();

            var subscribePacket = new MqttSubscribePacket
            {
                PacketIdentifier = GetNewPacketIdentifier(),
                TopicFilters = topicFilters
            };

            var response = await SendAndReceiveAsync<MqttSubAckPacket>(subscribePacket);

            if (response.SubscribeReturnCodes.Count != topicFilters.Count)
            {
                throw new MqttProtocolViolationException("The return codes are not matching the topic filters [MQTT-3.9.3-1].");
            }

            var result = new List<MqttSubscribeResult>();
            for (var i = 0; i < topicFilters.Count; i++)
            {
                result.Add(new MqttSubscribeResult(topicFilters[i], response.SubscribeReturnCodes[i]));
            }

            return result;
        }

        public async Task Unsubscribe(params string[] topicFilters)
        {
            if (topicFilters == null) throw new ArgumentNullException(nameof(topicFilters));

            await Unsubscribe(topicFilters.ToList());
        }

        public async Task Unsubscribe(IList<string> topicFilters)
        {
            if (topicFilters == null) throw new ArgumentNullException(nameof(topicFilters));
            if (!topicFilters.Any()) throw new MqttProtocolViolationException("At least one topic filter must be set [MQTT-3.10.3-2].");
            ThrowIfNotConnected();

            var unsubscribePacket = new MqttUnsubscribePacket
            {
                PacketIdentifier = GetNewPacketIdentifier(),
                TopicFilters = topicFilters
            };

            await SendAndReceiveAsync<MqttUnsubAckPacket>(unsubscribePacket);
        }

        public async Task PublishAsync(MqttApplicationMessage applicationMessage)
        {
            if (applicationMessage == null) throw new ArgumentNullException(nameof(applicationMessage));
            ThrowIfNotConnected();

            var publishPacket = applicationMessage.ToPublishPacket();

            if (publishPacket.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtMostOnce)
            {
                await SendAsync(publishPacket);
            }
            else if (publishPacket.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtLeastOnce)
            {
                publishPacket.PacketIdentifier = GetNewPacketIdentifier();
                await SendAndReceiveAsync<MqttPubAckPacket>(publishPacket);
            }
            else if (publishPacket.QualityOfServiceLevel == MqttQualityOfServiceLevel.ExactlyOnce)
            {
                publishPacket.PacketIdentifier = GetNewPacketIdentifier();
                await SendAndReceiveAsync<MqttPubRecPacket>(publishPacket);
                await SendAsync(publishPacket.CreateResponse<MqttPubCompPacket>());
            }
        }

        private void ThrowIfNotConnected()
        {
            if (!IsConnected) throw new MqttCommunicationException("The client is not connected.");
        }

        private async Task DisconnectInternalAsync()
        {
            try
            {
                await _adapter.DisconnectAsync();
            }
            catch
            {
            }
            finally
            {
                _cancellationTokenSource?.Cancel();
                IsConnected = false;
                Disconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        private async void ProcessReceivedPacket(MqttBasePacket mqttPacket)
        {
            try
            {
                if (mqttPacket is MqttPingReqPacket)
                {
                    await SendAsync(new MqttPingRespPacket());
                    return;
                }

                if (mqttPacket is MqttDisconnectPacket)
                {
                    await DisconnectAsync();
                    return;
                }

                var publishPacket = mqttPacket as MqttPublishPacket;
                if (publishPacket != null)
                {
                    await ProcessReceivedPublishPacket(publishPacket);
                    return;
                }

                var pubRelPacket = mqttPacket as MqttPubRelPacket;
                if (pubRelPacket != null)
                {
                    await ProcessReceivedPubRelPacket(pubRelPacket);
                    return;
                }

                _packetDispatcher.Dispatch(mqttPacket);
            }
            catch (Exception exception)
            {
                MqttTrace.Error(nameof(MqttClient), exception, "Error while processing received packet.");
            }
        }

        private void FireApplicationMessageReceivedEvent(MqttPublishPacket publishPacket)
        {
            if (publishPacket.QualityOfServiceLevel != MqttQualityOfServiceLevel.AtMostOnce)
            {
                _processedPublishPackets.Add(publishPacket.PacketIdentifier);
            }

            var applicationMessage = new MqttApplicationMessage(
                publishPacket.Topic,
                publishPacket.Payload,
                publishPacket.QualityOfServiceLevel,
                publishPacket.Retain
            );

            ApplicationMessageReceived?.Invoke(this, new MqttApplicationMessageReceivedEventArgs(applicationMessage));
        }

        private async Task ProcessReceivedPublishPacket(MqttPublishPacket publishPacket)
        {
            if (publishPacket.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtMostOnce)
            {
                FireApplicationMessageReceivedEvent(publishPacket);
            }
            else
            {
                if (publishPacket.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtLeastOnce)
                {
                    FireApplicationMessageReceivedEvent(publishPacket);
                    await SendAsync(new MqttPubAckPacket { PacketIdentifier = publishPacket.PacketIdentifier });
                }
                else if (publishPacket.QualityOfServiceLevel == MqttQualityOfServiceLevel.ExactlyOnce)
                {
                    _pendingExactlyOncePublishPackets[publishPacket.PacketIdentifier] = publishPacket;
                    await SendAsync(new MqttPubRecPacket { PacketIdentifier = publishPacket.PacketIdentifier });
                }
            }
        }

        private async Task ProcessReceivedPubRelPacket(MqttPubRelPacket pubRelPacket)
        {
            MqttPublishPacket originalPublishPacket;
            if (!_pendingExactlyOncePublishPackets.TryRemove(pubRelPacket.PacketIdentifier, out originalPublishPacket))
            {
                throw new MqttCommunicationException();
            }

            await SendAsync(originalPublishPacket.CreateResponse<MqttPubCompPacket>());

            FireApplicationMessageReceivedEvent(originalPublishPacket);
        }

        private async Task SendAsync(MqttBasePacket packet)
        {
            await _adapter.SendPacketAsync(packet, _options.DefaultCommunicationTimeout);
        }

        private async Task<TResponsePacket> SendAndReceiveAsync<TResponsePacket>(MqttBasePacket requestPacket) where TResponsePacket : MqttBasePacket
        {
            Func<MqttBasePacket, bool> responsePacketSelector = p =>
            {
                var p1 = p as TResponsePacket;
                if (p1 == null)
                {
                    return false;
                }

                var pi1 = requestPacket as IPacketWithIdentifier;
                var pi2 = p as IPacketWithIdentifier;

                if (pi1 != null && pi2 != null)
                {
                    if (pi1.PacketIdentifier != pi2.PacketIdentifier)
                    {
                        return false;
                    }
                }

                return true;
            };

            await _adapter.SendPacketAsync(requestPacket, _options.DefaultCommunicationTimeout);
            return (TResponsePacket)await _packetDispatcher.WaitForPacketAsync(responsePacketSelector, _options.DefaultCommunicationTimeout);
        }

        private ushort GetNewPacketIdentifier()
        {
            return (ushort)Interlocked.Increment(ref _latestPacketIdentifier);
        }

        private async Task SendKeepAliveMessagesAsync(CancellationToken cancellationToken)
        {
            MqttTrace.Information(nameof(MqttClient), "Start sending keep alive packets.");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(_options.KeepAlivePeriod, cancellationToken);
                    await SendAndReceiveAsync<MqttPingRespPacket>(new MqttPingReqPacket());
                }
            }
            catch (MqttCommunicationException)
            {
            }
            catch (Exception exception)
            {
                MqttTrace.Warning(nameof(MqttClient), exception, "Error while sending/receiving keep alive packets.");
            }
            finally
            {
                MqttTrace.Information(nameof(MqttClient), "Stopped sending keep alive packets.");
                await DisconnectInternalAsync();
            }
        }

        private async Task ReceivePackets(CancellationToken cancellationToken)
        {
            MqttTrace.Information(nameof(MqttClient), "Start receiving packets.");
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var mqttPacket = await _adapter.ReceivePacketAsync(TimeSpan.Zero);
                    MqttTrace.Information(nameof(MqttClient), $"Received <<< {mqttPacket}");

                    Task.Run(() => ProcessReceivedPacket(mqttPacket), cancellationToken).Forget();
                }
            }
            catch (MqttCommunicationException)
            {
            }
            catch (Exception exception)
            {
                MqttTrace.Error(nameof(MqttClient), exception, "Error while receiving packets.");
            }
            finally
            {
                MqttTrace.Information(nameof(MqttClient), "Stopped receiving packets.");
                await DisconnectInternalAsync();
            }
        }
    }
}