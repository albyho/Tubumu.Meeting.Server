using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using SignalRSwaggerGen.Attributes;
using Tubumu.Core.Extensions;
using Tubumu.Mediasoup;
using Tubumu.Mediasoup.Extensions;

namespace Tubumu.Meeting.Server
{
    [Authorize]
    [SignalRHub(path: "/hubs/meetingHub")]
    public partial class MeetingHub : Hub<IPeer>
    {
        private readonly ILogger<MeetingHub> _logger;
        private readonly IHubContext<MeetingHub, IPeer> _hubContext;
        private readonly BadDisconnectSocketService _badDisconnectSocketService;
        private readonly Scheduler _scheduler;
        private readonly MeetingServerOptions _meetingServerOptions;

        private string UserId => Context.UserIdentifier;
        private string ConnectionId => Context.ConnectionId;

        public MeetingHub(ILogger<MeetingHub> logger,
            IHubContext<MeetingHub, IPeer> hubContext,
            BadDisconnectSocketService badDisconnectSocketService,
            Scheduler scheduler,
            MeetingServerOptions meetingServerOptions)
        {
            _logger = logger;
            _hubContext = hubContext;
            _scheduler = scheduler;
            _badDisconnectSocketService = badDisconnectSocketService;
            _meetingServerOptions = meetingServerOptions;
        }

        public override async Task OnConnectedAsync()
        {
            await LeaveAsync();
            _badDisconnectSocketService.CacheContext(Context);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            await LeaveAsync();
            await base.OnDisconnectedAsync(exception);
        }

        #region Private

        private async Task LeaveAsync()
        {
            var leaveResult = await _scheduler.LeaveAsync(UserId);
            if (leaveResult != null)
            {
                // Notification: peerLeaveRoom
                SendNotification(leaveResult.OtherPeerIds, "peerLeaveRoom", new { PeerId = leaveResult.SelfPeer.PeerId });
                _badDisconnectSocketService.DisconnectClient(leaveResult.SelfPeer.ConnectionId);
            }
        }

        #endregion Private
    }

    public partial class MeetingHub
    {
        #region Room

        /// <summary>
        /// Get RTP capabilities of router.
        /// </summary>
        /// <returns></returns>
        [SignalRMethod(name: "GetRouterRtpCapabilities", operationType: OperationType.Get)]
        public MeetingMessage<RtpCapabilities> GetRouterRtpCapabilities()
        {
            var rtpCapabilities = _scheduler.DefaultRtpCapabilities;
            return new MeetingMessage<RtpCapabilities> { Code = 200, Message = "GetRouterRtpCapabilities 成功", Data = rtpCapabilities };
        }

        /// <summary>
        /// Join meeting.
        /// </summary>
        /// <param name="joinRequest"></param>
        /// <returns></returns>
        [SignalRMethod(name: "Join", operationType: OperationType.Post)]
        public async Task<MeetingMessage> Join([SignalRArg] JoinRequest joinRequest)
        {
            if (!await _scheduler.JoinAsync(UserId, ConnectionId, joinRequest))
            {
                return new MeetingMessage { Code = 400, Message = "Join 失败" };
            }

            return new MeetingMessage { Code = 200, Message = "Join 成功" };
        }

        /// <summary>
        /// Join room.
        /// </summary>
        /// <param name="joinRoomRequest"></param>
        /// <returns></returns>
        [SignalRMethod(name: "JoinRoom", operationType: OperationType.Post)]
        public async Task<MeetingMessage<JoinRoomResponse>> JoinRoom([SignalRArg] JoinRoomRequest joinRoomRequest)
        {
            // FIXME: (alby)明文告知用户进入房间的 Role 存在安全问题, 特别是 Invite 模式下。
            var joinRoomResult = await _scheduler.JoinRoomAsync(UserId, ConnectionId, joinRoomRequest);
            if(joinRoomResult == null)
            {
                return new MeetingMessage<JoinRoomResponse> { Code = 400, Message = "JoinRoom 失败" };
            }

            // 将自身的信息告知给房间内的其他人
            var otherPeerIds = joinRoomResult.Peers.Select(m => m.PeerId).Where(m => m != joinRoomResult.SelfPeer.PeerId).ToArray();
            // Notification: peerJoinRoom
            SendNotification(otherPeerIds, "peerJoinRoom", new
            {
                Peer = joinRoomResult.SelfPeer
            });

            // 返回包括自身的房间内的所有人的信息
            var data = new JoinRoomResponse
            {
                Peers = joinRoomResult.Peers,
            };
            return new MeetingMessage<JoinRoomResponse> { Code = 200, Message = "JoinRoom 成功", Data = data };
        }

        /// <summary>
        /// Leave room.
        /// </summary>
        /// <returns></returns>
        [SignalRMethod(name: "LeaveRoom", operationType: OperationType.Get)]
        public async Task<MeetingMessage> LeaveRoom()
        {
            try
            {
                var leaveRoomResult = await _scheduler.LeaveRoomAsync(UserId, ConnectionId);

                // Notification: peerLeaveRoom
                SendNotification(leaveRoomResult.OtherPeerIds, "peerLeaveRoom", new
                {
                    PeerId = UserId
                });

                return new MeetingMessage { Code = 200, Message = "LeaveRoom 成功" };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LeaveRoom 调用失败.");
                return new MeetingMessage { Code = 400, Message = "LeaveRoom 失败" };
            }
        }

        #endregion

        #region Transport

        /// <summary>
        /// Create send WebRTC transport.
        /// </summary>
        /// <param name="createWebRtcTransportRequest"></param>
        /// <returns></returns>
        [SignalRMethod(name: "CreateSendWebRtcTransport", operationType: OperationType.Post)]
        public Task<MeetingMessage<CreateWebRtcTransportResult>> CreateSendWebRtcTransport([SignalRArg] CreateWebRtcTransportRequest createWebRtcTransportRequest)
        {
            return CreateWebRtcTransportAsync(createWebRtcTransportRequest, true);
        }

        /// <summary>
        /// Create recv WebRTC transport.
        /// </summary>
        /// <param name="createWebRtcTransportRequest"></param>
        /// <returns></returns>
        [SignalRMethod(name: "CreateRecvWebRtcTransport", operationType: OperationType.Post)]
        public Task<MeetingMessage<CreateWebRtcTransportResult>> CreateRecvWebRtcTransport([SignalRArg] CreateWebRtcTransportRequest createWebRtcTransportRequest)
        {
            return CreateWebRtcTransportAsync(createWebRtcTransportRequest, false);
        }

        /// <summary>
        /// Create WebRTC transport.
        /// </summary>
        /// <param name="createWebRtcTransportRequest"></param>
        /// <returns></returns>
        private async Task<MeetingMessage<CreateWebRtcTransportResult>> CreateWebRtcTransportAsync([SignalRArg] CreateWebRtcTransportRequest createWebRtcTransportRequest, bool isSend)
        {
            var transport = await _scheduler.CreateWebRtcTransportAsync(UserId, ConnectionId, createWebRtcTransportRequest, isSend);
            transport.On("sctpstatechange", sctpState =>
            {
                _logger.LogDebug($"WebRtcTransport \"sctpstatechange\" event [sctpState:{sctpState}]");
                return Task.CompletedTask;
            });

            transport.On("dtlsstatechange", value =>
            {
                var dtlsState = (DtlsState)value!;
                if (dtlsState == DtlsState.Failed || dtlsState == DtlsState.Closed)
                {
                    _logger.LogWarning($"WebRtcTransport dtlsstatechange event [dtlsState:{value}]");
                }
                return Task.CompletedTask;
            });

            // NOTE: For testing.
            //await transport.EnableTraceEventAsync(new[] { TransportTraceEventType.Probation, TransportTraceEventType.BWE });
            //await transport.EnableTraceEventAsync(new[] { TransportTraceEventType.BWE });

            var peerId = UserId;
            transport.On("trace", trace =>
            {
                var traceData = (TransportTraceEventData)trace!;
                _logger.LogDebug($"transport \"trace\" event [transportId:{transport.TransportId}, trace:{traceData.Type.GetEnumMemberValue()}]");

                if (traceData.Type == TransportTraceEventType.BWE && traceData.Direction == TraceEventDirection.Out)
                {
                    // Notification: downlinkBwe
                    SendNotification(peerId, "downlinkBwe", new
                    {
                        DesiredBitrate = traceData.Info["desiredBitrate"],
                        EffectiveDesiredBitrate = traceData.Info["effectiveDesiredBitrate"],
                        AvailableBitrate = traceData.Info["availableBitrate"]
                    });
                }
                return Task.CompletedTask;
            });

            return new MeetingMessage<CreateWebRtcTransportResult>
            {
                Code = 200,
                Message = $"CreateWebRtcTransport 成功({(isSend ? "Producing" : "Consuming")})",
                Data = new CreateWebRtcTransportResult
                {
                    TransportId = transport.TransportId,
                    IceParameters = transport.IceParameters,
                    IceCandidates = transport.IceCandidates,
                    DtlsParameters = transport.DtlsParameters,
                    SctpParameters = transport.SctpParameters,
                }
            };
        }

        /// <summary>
        /// Connect WebRTC transport.
        /// </summary>
        /// <param name="connectWebRtcTransportRequest"></param>
        /// <returns></returns>
        [SignalRMethod(name: "ConnectWebRtcTransport", operationType: OperationType.Post)]
        public async Task<MeetingMessage> ConnectWebRtcTransport([SignalRArg] ConnectWebRtcTransportRequest connectWebRtcTransportRequest)
        {
            try
            {
                if (!await _scheduler.ConnectWebRtcTransportAsync(UserId, ConnectionId, connectWebRtcTransportRequest))
                {
                    return new MeetingMessage { Code = 400, Message = $"ConnectWebRtcTransport 失败: TransportId: {connectWebRtcTransportRequest.TransportId}" };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConnectWebRtcTransport()");
                return new MeetingMessage { Code = 400, Message = $"ConnectWebRtcTransport 失败: TransportId: {connectWebRtcTransportRequest.TransportId}, {ex.Message}" };
            }

            return new MeetingMessage { Code = 200, Message = "ConnectWebRtcTransport 成功" };
        }

        [SignalRMethod(name: "Ready", operationType: OperationType.Post)]
        public async Task<MeetingMessage> Ready()
        {
            if (_meetingServerOptions.ServeMode == ServeMode.Open || _meetingServerOptions.ServeMode == ServeMode.Invite)
            {
                try
                {
                    var otherPeers = await _scheduler.GetOtherPeersAsync(UserId, ConnectionId);
                    foreach (var producerPeer in otherPeers.Where(m => m.PeerId != UserId))
                    {
                        var producers = await producerPeer.GetProducersASync();
                        foreach (var producer in producers.Values)
                        {
                            // 本 Peer 消费其他 Peer
                            CreateConsumer(UserId, producerPeer.PeerId, producer).ContinueWithOnFaultedHandleLog(_logger);
                        }
                    }
                }
                catch(Exception ex)
                {
                    _logger.LogError(ex, "Ready()");
                    // Ignore
                }
            }

            return new MeetingMessage { Code = 200, Message = "Ready 成功" };
        }

        #endregion

        #region Pull mode

        /// <summary>
        /// Pull medias.
        /// </summary>
        /// <param name="pullRequest"></param>
        /// <returns></returns>
        [SignalRMethod(name: "Pull", operationType: OperationType.Post)]
        public async Task<MeetingMessage> Pull([SignalRArg] PullRequest pullRequest)
        {
            if (_meetingServerOptions.ServeMode != ServeMode.Pull)
            {
                throw new NotSupportedException($"Not supported on \"{_meetingServerOptions.ServeMode}\" mode. Needs \"{ServeMode.Pull}\" mode.");
            }

            var consumeResult = await _scheduler.PullAsync(UserId, ConnectionId, pullRequest);
            var consumerPeer = consumeResult.ConsumePeer;
            var producerPeer = consumeResult.ProducePeer;

            foreach (var producer in consumeResult.ExistsProducers)
            {
                // 本 Peer 消费其他 Peer
                CreateConsumer(consumerPeer.PeerId, producerPeer.PeerId, producer).ContinueWithOnFaultedHandleLog(_logger);
            }

            if (!consumeResult.ProduceSources.IsNullOrEmpty())
            {
                // Notification: produceSources
                SendNotification(consumeResult.ConsumePeer.PeerId, "produceSources", new
                {
                    ProduceSources = consumeResult.ProduceSources
                });
            }

            return new MeetingMessage { Code = 200, Message = "Pull 成功" };
        }

        #endregion

        #region Invite mode

        /// <summary>
        /// Invite medias.
        /// </summary>
        /// <param name="inviteRequest"></param>
        /// <returns></returns>
        [SignalRMethod(name: "Invite", operationType: OperationType.Post)]
        public async Task<MeetingMessage> Invite([SignalRArg] InviteRequest inviteRequest)
        {
            if (_meetingServerOptions.ServeMode != ServeMode.Invite)
            {
                throw new NotSupportedException($"Not supported on \"{_meetingServerOptions.ServeMode}\" mode. Needs \"{ServeMode.Invite}\" mode.");
            }

            // 仅会议室管理员可以邀请。
            if(await _scheduler.GetPeerRoleAsync(UserId, ConnectionId) != UserRole.Admin)
            {
                return new MeetingMessage { Code = 400, Message = "仅管理员可发起邀请。" };
            }

            // 管理员无需邀请自己。
            if(inviteRequest.PeerId == UserId)
            {
                return new MeetingMessage { Code = 400, Message = "管理员请勿邀请自己。" };
            }

            if (inviteRequest.Sources.IsNullOrEmpty()||inviteRequest.Sources.Any(m => m.IsNullOrWhiteSpace()))
            {
                return new MeetingMessage<ProduceRespose>
                {
                    Code = 400,
                    Message = $"Invite 失败: Sources 参数缺失或非法。",
                };
            }

            // NOTE: 暂未校验被邀请方是否有对应的 Source 。不过就算接收邀请也无法生产。

            var setPeerInternalDataRequest = new SetPeerInternalDataRequest
            {
                PeerId = inviteRequest.PeerId,
                InternalData = new Dictionary<string, object>()
            };
            foreach (var source in inviteRequest.Sources)
            {
                setPeerInternalDataRequest.InternalData[$"Invate:{source}"] = true;
            };

            await _scheduler.SetPeerInternalDataAsync(setPeerInternalDataRequest);

            // Notification: produceSources
            SendNotification(inviteRequest.PeerId, "produceSources", new
            {
                ProduceSources = inviteRequest.Sources
            });

            return new MeetingMessage { Code = 200, Message = "Invite 成功" };
        }

        /// <summary>
        /// Deinvite medias.
        /// </summary>
        /// <param name="inviteRequest"></param>
        /// <returns></returns>
        [SignalRMethod(name: "Deinvite", operationType: OperationType.Post)]
        public async Task<MeetingMessage> Deinvite([SignalRArg] DeinviteRequest deinviteRequest)
        {
            if (_meetingServerOptions.ServeMode != ServeMode.Invite)
            {
                throw new NotSupportedException($"Not supported on \"{_meetingServerOptions.ServeMode}\" mode. Needs \"{ServeMode.Invite}\" mode.");
            }

            // 仅会议室管理员可以取消邀请。
            if (await _scheduler.GetPeerRoleAsync(UserId, ConnectionId) != UserRole.Admin)
            {
                return new MeetingMessage { Code = 400, Message = "仅管理员可取消邀请。" };
            }

            // 管理员无需取消邀请自己。
            if (deinviteRequest.PeerId == UserId)
            {
                return new MeetingMessage { Code = 400, Message = "管理员请勿取消邀请自己。" };
            }

            if (deinviteRequest.Sources.IsNullOrEmpty() || deinviteRequest.Sources.Any(m => m.IsNullOrWhiteSpace()))
            {
                return new MeetingMessage<ProduceRespose>
                {
                    Code = 400,
                    Message = $"Deinvite 失败: Sources 参数缺失或非法。",
                };
            }

            // NOTE: 暂未校验被邀请方是否有对应的 Source 。也未校验对应 Source 是否收到邀请。

            var unSetPeerInternalDataRequest = new UnsetPeerInternalDataRequest
            {
                PeerId = deinviteRequest.PeerId,
            };
            var keys = new List<string>();
            foreach (var source in deinviteRequest.Sources)
            {
                keys.Add($"Invate:{source}");
            };
            unSetPeerInternalDataRequest.Keys = keys.ToArray();

            await _scheduler.UnsetPeerInternalDataAsync(unSetPeerInternalDataRequest);

            await _scheduler.CloseProducerWithSourcesAsync(deinviteRequest.PeerId, deinviteRequest.Sources);

            // Notification: closeSources
            SendNotification(deinviteRequest.PeerId, "closeSources", new
            {
                CloseSources = deinviteRequest.Sources
            });

            return new MeetingMessage { Code = 200, Message = "Deinvite 成功" };
        }

        /// <summary>
        /// Request produce medias.
        /// </summary>
        /// <param name="inviteRequest"></param>
        /// <returns></returns>
        [SignalRMethod(name: "RequestProduce", operationType: OperationType.Post)]
        public async Task<MeetingMessage> RequestProduce([SignalRArg] RequestProduceRequest requestProduceRequest)
        {
            if (_meetingServerOptions.ServeMode != ServeMode.Invite)
            {
                throw new NotSupportedException($"Not supported on \"{_meetingServerOptions.ServeMode}\" mode. Needs \"{ServeMode.Invite}\" mode.");
            }

            // 管理员无需发出申请。
            if (await _scheduler.GetPeerRoleAsync(UserId, ConnectionId) == UserRole.Admin)
            {
                return new MeetingMessage { Code = 400, Message = "管理员无需发出申请。" };
            }

            if (requestProduceRequest.Sources.IsNullOrEmpty() || requestProduceRequest.Sources.Any(m => m.IsNullOrWhiteSpace()))
            {
                return new MeetingMessage<ProduceRespose>
                {
                    Code = 400,
                    Message = $"RequestProduce 失败: Sources 参数缺失或非法。",
                };
            }

            // NOTE: 暂未校验被邀请方是否有对应的 Source 。不过就算接收邀请也无法生产。

            var adminIds = await _scheduler.GetOtherPeerIdsAsync(UserId, ConnectionId, UserRole.Admin);

            // Notification: requestProduce
            SendNotification(adminIds, "requestProduce", new
            {
                PeerId = UserId,
                ProduceSources = requestProduceRequest.Sources
            });

            return new MeetingMessage { Code = 200, Message = "RequestProduce 成功" };
        }

        #endregion

        #region Producer

        /// <summary>
        /// Produce media.
        /// </summary>
        /// <param name="produceRequest"></param>
        /// <returns></returns>
        [SignalRMethod(name: "Produce", operationType: OperationType.Post)]
        public async Task<MeetingMessage<ProduceRespose>> Produce([SignalRArg] ProduceRequest produceRequest)
        {
            // HACK: (alby) Android 传入 RtpParameters 有误的临时处理方案
            if (produceRequest.Kind == MediaKind.Audio)
            {
                produceRequest.RtpParameters.Codecs[0].Channels = 2;
                produceRequest.RtpParameters.Codecs[0].PayloadType = 111;
            }
            var peerId = UserId;
            ProduceResult produceResult;
            try
            {
                // 在 Invate 模式下如果不是管理员需校验 Source 是否被邀请。
                if (_meetingServerOptions.ServeMode == ServeMode.Invite && await _scheduler.GetPeerRoleAsync(UserId, ConnectionId) != UserRole.Admin)
                {
                    var internalData = await _scheduler.GetPeerInternalDataAsync(UserId, ConnectionId);
                    var inviteKey = $"Invate:{produceRequest.Source}";
                    if(!internalData.InternalData.TryGetValue(inviteKey, out var inviteValue)) {
                        return new MeetingMessage<ProduceRespose>
                        {
                            Code = 400,
                            Message = $"Produce 失败:未受邀请的生产。",
                        };
                    }

                    // 清除邀请状态
                    await _scheduler.UnsetPeerInternalDataAsync(new UnsetPeerInternalDataRequest
                    {
                        PeerId = UserId,
                        Keys = new[] { inviteKey }
                    });
                }
                produceResult = await _scheduler.ProduceAsync(peerId, ConnectionId, produceRequest);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Produce()");
                return new MeetingMessage<ProduceRespose>
                {
                    Code = 400,
                    Message = $"Produce 失败:{ex.Message}",
                };
            }

            var producerPeer = produceResult.ProducerPeer;
            var producer = produceResult.Producer;
            var otherPeers = _meetingServerOptions.ServeMode == ServeMode.Pull ?
                produceResult.PullPaddingConsumerPeers : await producerPeer.GetOtherPeersAsync();

            foreach (var consumerPeer in otherPeers)
            {
                // 其他 Peer 消费本 Peer
                CreateConsumer(consumerPeer.PeerId, producerPeer.PeerId, producer).ContinueWithOnFaultedHandleLog(_logger);
            }

            // NOTE: For Testing
            //CreateConsumer(producerPeer, producerPeer, producer, "1").ContinueWithOnFaultedHandleLog(_logger);

            // Set Producer events.
            producer.On("score", score =>
            {
                var data = (ProducerScore[])score!;
                // Notification: producerScore
                SendNotification(peerId, "producerScore", new { ProducerId = producer.ProducerId, Score = data });
                return Task.CompletedTask;
            });
            producer.On("videoorientationchange", videoOrientation =>
            {
                // Notification: videoorientationchange
                var data = (ProducerVideoOrientation)videoOrientation!;
                _logger.LogDebug($"producer.On() | Producer \"videoorientationchange\" Event [producerId:\"{producer.ProducerId}\", VideoOrientation:\"{videoOrientation}\"]");
                return Task.CompletedTask;
            });
            producer.Observer.On("close", _ =>
            {
                // Notification: producerClosed
                SendNotification(peerId, "producerClosed", new { ProducerId = producer.ProducerId });
                return Task.CompletedTask;
            });

            return new MeetingMessage<ProduceRespose>
            {
                Code = 200,
                Message = "Produce 成功",
                Data = new ProduceRespose { Id = producer.ProducerId, Source = produceRequest.Source }
            };
        }

        /// <summary>
        /// Close producer.
        /// </summary>
        /// <param name="producerId"></param>
        /// <returns></returns>
        [SignalRMethod(name: "CloseProducer", operationType: OperationType.Post)]
        public async Task<MeetingMessage> CloseProducer([SignalRArg] string producerId)
        {
            if (!await _scheduler.CloseProducerAsync(UserId, ConnectionId, producerId))
            {
                return new MeetingMessage { Code = 400, Message = "CloseProducer 失败" };
            }

            return new MeetingMessage { Code = 200, Message = "CloseProducer 成功" };
        }

        /// <summary>
        /// Pause producer.
        /// </summary>
        /// <param name="producerId"></param>
        /// <returns></returns>
        [SignalRMethod(name: "PauseProducer", operationType: OperationType.Post)]
        public async Task<MeetingMessage> PauseProducer([SignalRArg] string producerId)
        {
            if (!await _scheduler.PauseProducerAsync(UserId, ConnectionId, producerId))
            {
                return new MeetingMessage { Code = 400, Message = "CloseProducer 失败" };
            }

            return new MeetingMessage { Code = 200, Message = "PauseProducer 成功" };
        }

        /// <summary>
        /// Resume producer.
        /// </summary>
        /// <param name="producerId"></param>
        /// <returns></returns>
        [SignalRMethod(name: "ResumeProducer", operationType: OperationType.Post)]
        public async Task<MeetingMessage> ResumeProducer([SignalRArg] string producerId)
        {
            if (!await _scheduler.ResumeProducerAsync(UserId, ConnectionId, producerId))
            {
                return new MeetingMessage { Code = 400, Message = "CloseProducer 失败" };
            }

            return new MeetingMessage { Code = 200, Message = "ResumeProducer 成功" };
        }

        #endregion

        #region Consumer

        /// <summary>
        /// Close consumer.
        /// </summary>
        /// <param name="consumerId"></param>
        /// <returns></returns>
        [SignalRMethod(name: "CloseConsumer", operationType: OperationType.Post)]
        public async Task<MeetingMessage> CloseConsumer([SignalRArg] string consumerId)
        {
            if (!await _scheduler.CloseConsumerAsync(UserId, ConnectionId, consumerId))
            {
                return new MeetingMessage { Code = 400, Message = "CloseConsumer 失败" };
            }

            return new MeetingMessage { Code = 200, Message = "CloseConsumer 成功" };
        }

        /// <summary>
        /// Pause consumer.
        /// </summary>
        /// <param name="consumerId"></param>
        /// <returns></returns>
        [SignalRMethod(name: "PauseConsumer", operationType: OperationType.Post)]
        public async Task<MeetingMessage> PauseConsumer([SignalRArg] string consumerId)
        {
            if (!await _scheduler.PauseConsumerAsync(UserId, ConnectionId, consumerId))
            {
                return new MeetingMessage { Code = 400, Message = "PauseConsumer 失败" };
            }

            return new MeetingMessage { Code = 200, Message = "PauseConsumer 成功" };
        }

        /// <summary>
        /// Resume consumer.
        /// </summary>
        /// <param name="consumerId"></param>
        /// <returns></returns>
        [SignalRMethod(name: "ResumeConsumer", operationType: OperationType.Post)]
        public async Task<MeetingMessage> ResumeConsumer([SignalRArg] string consumerId)
        {
            try
            {
                var consumer = await _scheduler.ResumeConsumerAsync(UserId, ConnectionId, consumerId);
                if (consumer == null)
                {
                    return new MeetingMessage { Code = 400, Message = "ResumeConsumer 失败" };
                }

                // Notification: consumerScore
                SendNotification(UserId, "consumerScore", new { ConsumerId = consumer.ConsumerId, Score = consumer.Score });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ResumeConsumer()");
                return new MeetingMessage
                {
                    Code = 400,
                    Message = $"ResumeConsumer 失败:{ex.Message}",
                };
            }

            return new MeetingMessage { Code = 200, Message = "ResumeConsumer 成功" };
        }

        /// <summary>
        /// Set consumer's preferedLayers.
        /// </summary>
        /// <param name="setConsumerPreferedLayersRequest"></param>
        /// <returns></returns>
        [SignalRMethod(name: "SetConsumerPreferedLayers", operationType: OperationType.Post)]
        public async Task<MeetingMessage> SetConsumerPreferedLayers([SignalRArg] SetConsumerPreferedLayersRequest setConsumerPreferedLayersRequest)
        {
            if (!await _scheduler.SetConsumerPreferedLayersAsync(UserId, ConnectionId, setConsumerPreferedLayersRequest))
            {
                return new MeetingMessage { Code = 400, Message = "SetConsumerPreferedLayers 失败" };
            }

            return new MeetingMessage { Code = 200, Message = "SetConsumerPreferedLayers 成功" };
        }

        /// <summary>
        /// Set consumer's priority.
        /// </summary>
        /// <param name="setConsumerPriorityRequest"></param>
        /// <returns></returns>
        [SignalRMethod(name: "SetConsumerPriority", operationType: OperationType.Post)]
        public async Task<MeetingMessage> SetConsumerPriority([SignalRArg] SetConsumerPriorityRequest setConsumerPriorityRequest)
        {
            if (!await _scheduler.SetConsumerPriorityAsync(UserId, ConnectionId, setConsumerPriorityRequest))
            {
                return new MeetingMessage { Code = 400, Message = "SetConsumerPreferedLayers 失败" };
            }

            return new MeetingMessage { Code = 200, Message = "SetConsumerPriority 成功" };
        }

        /// <summary>
        /// Request key-frame.
        /// </summary>
        /// <param name="consumerId"></param>
        /// <returns></returns>
        [SignalRMethod(name: "RequestConsumerKeyFrame", operationType: OperationType.Post)]
        public async Task<MeetingMessage> RequestConsumerKeyFrame([SignalRArg] string consumerId)
        {
            if (!await _scheduler.RequestConsumerKeyFrameAsync(UserId, ConnectionId, consumerId))
            {
                return new MeetingMessage { Code = 400, Message = "RequestConsumerKeyFrame 失败" };
            }

            return new MeetingMessage { Code = 200, Message = "RequestConsumerKeyFrame 成功" };
        }

        #endregion

        #region Stats

        /// <summary>
        /// Get transport's state.
        /// </summary>
        /// <param name="transportId"></param>
        /// <returns></returns>
        [SignalRMethod(name: "GetWebRtcTransportStats", operationType: OperationType.Post)]
        public async Task<MeetingMessage<TransportStat>> GetWebRtcTransportStats([SignalRArg] string transportId)
        {
            var data = await _scheduler.GetWebRtcTransportStatsAsync(UserId, ConnectionId, transportId);
            return new MeetingMessage<TransportStat> { Code = 200, Message = "GetWebRtcTransportStats 成功", Data = data };
        }

        /// <summary>
        /// Get producer's state.
        /// </summary>
        /// <param name="producerId"></param>
        /// <returns></returns>
        [SignalRMethod(name: "GetProducerStats", operationType: OperationType.Post)]
        public async Task<MeetingMessage<ProducerStat>> GetProducerStats([SignalRArg] string producerId)
        {
            var data = await _scheduler.GetProducerStatsAsync(UserId, ConnectionId, producerId);
            return new MeetingMessage<ProducerStat> { Code = 200, Message = "GetProducerStats 成功", Data = data };
        }

        /// <summary>
        /// Get consumer's state.
        /// </summary>
        /// <param name="consumerId"></param>
        /// <returns></returns>
        [SignalRMethod(name: "GetConsumerStats", operationType: OperationType.Post)]
        public async Task<MeetingMessage<ConsumerStat>> GetConsumerStats([SignalRArg] string consumerId)
        {
            var data = await _scheduler.GetConsumerStatsAsync(UserId, ConnectionId, consumerId);
            return new MeetingMessage<ConsumerStat> { Code = 200, Message = "GetConsumerStats 成功", Data = data };
        }

        /// <summary>
        /// Restart ICE.
        /// </summary>
        /// <param name="transportId"></param>
        /// <returns></returns>
        [SignalRMethod(name: "RestartIce", operationType: OperationType.Post)]
        public async Task<MeetingMessage<IceParameters?>> RestartIce([SignalRArg] string transportId)
        {
            var iceParameters = await _scheduler.RestartIceAsync(UserId, ConnectionId, transportId);
            return new MeetingMessage<IceParameters?> { Code = 200, Message = "RestartIce 成功", Data = iceParameters };
        }

        #endregion

        #region Message

        /// <summary>
        /// Send message to other peers in rooms.
        /// </summary>
        /// <param name="sendMessageRequest"></param>
        /// <returns></returns>
        [SignalRMethod(name: "SendMessage", operationType: OperationType.Post)]
        public async Task<MeetingMessage> SendMessage([SignalRArg] SendMessageRequest sendMessageRequest)
        {
            var otherPeerIds = await _scheduler.GetOtherPeerIdsAsync(UserId, ConnectionId);

            // Notification: newMessage
            SendNotification(otherPeerIds, "newMessage", new
            {
                RoomId = sendMessageRequest.RoomId,
                Message = sendMessageRequest.Message,
            });

            return new MeetingMessage { Code = 200, Message = "SendMessage 成功" };
        }

        #endregion

        #region PeerAppData

        /// <summary>
        /// Set peer's appData. Then notify other peer, if in a room.
        /// </summary>
        /// <param name="setPeerAppDataRequest"></param>
        /// <returns></returns>
        [SignalRMethod(name: "SetPeerAppData", operationType: OperationType.Post)]
        public async Task<MeetingMessage> SetPeerAppData([SignalRArg] SetPeerAppDataRequest setPeerAppDataRequest)
        {
            var peerPeerAppDataResult = await _scheduler.SetPeerAppDataAsync(UserId, ConnectionId, setPeerAppDataRequest);

            // Notification: peerAppDataChanged
            SendNotification(peerPeerAppDataResult.OtherPeerIds, "peerAppDataChanged", new
            {
                PeerId = UserId,
                AppData = peerPeerAppDataResult.AppData,
            });

            return new MeetingMessage { Code = 200, Message = "SetRoomAppData 成功" };
        }

        /// <summary>
        /// Unset peer'ss appData. Then notify other peer, if in a room.
        /// </summary>
        /// <param name="unsetPeerAppDataRequest"></param>
        /// <returns></returns>
        [SignalRMethod(name: "UnsetPeerAppData", operationType: OperationType.Post)]
        public async Task<MeetingMessage> UnsetPeerAppData([SignalRArg] UnsetPeerAppDataRequest unsetPeerAppDataRequest)
        {
            var peerPeerAppDataResult = await _scheduler.UnsetPeerAppDataAsync(UserId, ConnectionId, unsetPeerAppDataRequest);

            // Notification: peerAppDataChanged
            SendNotification(peerPeerAppDataResult.OtherPeerIds, "peerAppDataChanged", new
            {
                PeerId = UserId,
                AppData = peerPeerAppDataResult.AppData,
            });

            return new MeetingMessage { Code = 200, Message = "UnsetPeerAppData 成功" };
        }

        /// <summary>
        /// Clear peer's appData. Then notify other peer, if in a room.
        /// </summary>
        /// <returns></returns>
        [SignalRMethod(name: "ClearPeerAppData", operationType: OperationType.Get)]
        public async Task<MeetingMessage> ClearPeerAppData()
        {
            var peerPeerAppDataResult = await _scheduler.ClearPeerAppDataAsync(UserId, ConnectionId);

            // Notification: peerAppDataChanged
            SendNotification(peerPeerAppDataResult.OtherPeerIds, "peerAppDataChanged", new
            {
                PeerId = UserId,
                AppData = peerPeerAppDataResult.AppData,
            });

            return new MeetingMessage { Code = 200, Message = "ClearPeerAppData 成功" };
        }

        #endregion

        #region Private Methods

        private async Task CreateConsumer(string consumerPeerId, string producerPeerId, Producer producer)
        {
            _logger.LogDebug($"CreateConsumer() | [ConsumerPeer:\"{consumerPeerId}\", ProducerPeer:\"{producerPeerId}\", Producer:\"{producer.ProducerId}\"]");

            // Create the Consumer in paused mode.
            Consumer consumer;

            try
            {
                consumer = await _scheduler.ConsumeAsync(producerPeerId, consumerPeerId, producer.ProducerId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CreateConsumer()");
                return;
            }

            // Set Consumer events.
            consumer.On("score", (score) =>
            {
                var data = (ConsumerScore)score!;
                // Notification: consumerScore
                SendNotification(consumerPeerId, "consumerScore", new { ConsumerId = consumer.ConsumerId, Score = data });
                return Task.CompletedTask;
            });

            // consumer.On("@close", _ => ...);
            // consumer.On("@producerclose", _ => ...);
            // consumer.On("producerclose", _ => ...);
            // consumer.On("transportclose", _ => ...);
            consumer.Observer.On("close", _ =>
            {
                // Notification: consumerClosed
                SendNotification(consumerPeerId, "consumerClosed", new { ConsumerId = consumer.ConsumerId });
                return Task.CompletedTask;
            });

            consumer.On("producerpause", _ =>
            {
                // Notification: consumerPaused
                SendNotification(consumerPeerId, "consumerPaused", new { ConsumerId = consumer.ConsumerId });
                return Task.CompletedTask;
            });

            consumer.On("producerresume", _ =>
            {
                // Notification: consumerResumed
                SendNotification(consumerPeerId, "consumerResumed", new { ConsumerId = consumer.ConsumerId });
                return Task.CompletedTask;
            });

            consumer.On("layerschange", layers =>
            {
                var data = (ConsumerLayers?)layers;

                // Notification: consumerLayersChanged
                SendNotification(consumerPeerId, "consumerLayersChanged", new { ConsumerId = consumer.ConsumerId });
                return Task.CompletedTask;
            });

            // NOTE: For testing.
            // await consumer.enableTraceEvent([ 'rtp', 'keyframe', 'nack', 'pli', 'fir' ]);
            // await consumer.enableTraceEvent([ 'pli', 'fir' ]);
            // await consumer.enableTraceEvent([ 'keyframe' ]);

            consumer.On("trace", trace =>
            {
                _logger.LogDebug($"consumer \"trace\" event [consumerId:{consumer.ConsumerId}, trace:{trace}]");
                return Task.CompletedTask;
            });

            // Send a request to the remote Peer with Consumer parameters.
            // Notification: newConsumer

            SendNotification(consumerPeerId, "newConsumer", new ConsumeInfo
            {
                ProducerPeerId = producerPeerId,
                Kind = consumer.Kind,
                ProducerId = producer.ProducerId,
                ConsumerId = consumer.ConsumerId,
                RtpParameters = consumer.RtpParameters,
                Type = consumer.Type,
                ProducerAppData = producer.AppData,
                ProducerPaused = consumer.ProducerPaused,
            });
        }

        private void SendNotification(string peerId, string type, object data)
        {
            // For Testing
            if (type == "consumerLayersChanged" || type == "consumerScore" || type == "producerScore") return;
            var client = _hubContext.Clients.User(peerId);
            client.Notify(new MeetingNotification
            {
                Type = type,
                Data = data
            }).ContinueWithOnFaultedHandleLog(_logger);
        }

        private void SendNotification(IReadOnlyList<string> peerIds, string type, object data)
        {
            if (peerIds.IsNullOrEmpty()) return;

            var client = _hubContext.Clients.Users(peerIds);
            client.Notify(new MeetingNotification
            {
                Type = type,
                Data = data
            }).ContinueWithOnFaultedHandleLog(_logger);
        }

        #endregion Private Methods
    }
}
