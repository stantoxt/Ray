﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Ray.Core.Configuration;
using Ray.Core.Event;
using Ray.Core.Exceptions;
using Ray.Core.Serialization;
using Ray.Core.Snapshot;
using Ray.Core.Storage;
using Ray.Core.Utils.Emit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Ray.Core
{
    public abstract class ObserverGrain<MainGrain, PrimaryKey> : Grain, IObserver
    {
        readonly Func<object, IEvent, EventBase, Task> handlerInvokeFunc;
        readonly HandlerAttribute handlerAttribute;
        public ObserverGrain()
        {
            GrainType = GetType();
            var handlerAttributes = GrainType.GetCustomAttributes(typeof(HandlerAttribute), false);
            if (handlerAttributes.Length > 0)
                handlerAttribute = (HandlerAttribute)handlerAttributes[0];
            else
                handlerAttribute = default;
            var methods = GetType().GetMethods().Where(m =>
            {
                var parameters = m.GetParameters();
                return parameters.Length >= 1 && parameters.Any(p => typeof(IEvent).IsAssignableFrom(p.ParameterType));
            }).ToList();
            var dynamicMethod = new DynamicMethod($"Handler_Invoke", typeof(Task), new Type[] { typeof(object), typeof(IEvent), typeof(EventBase) }, GrainType, true);
            var ilGen = dynamicMethod.GetILGenerator();
            var items = new List<SwitchMethodEmit>();
            for (int i = 0; i < methods.Count; i++)
            {
                var method = methods[i];
                var methodParams = method.GetParameters();
                var caseType = methodParams.Single(p => typeof(IEvent).IsAssignableFrom(p.ParameterType)).ParameterType;
                items.Add(new SwitchMethodEmit
                {
                    Mehod = method,
                    CaseType = caseType,
                    DeclareLocal = ilGen.DeclareLocal(caseType),
                    Lable = ilGen.DefineLabel(),
                    Parameters = methodParams,
                    Index = i
                });
            }
            var defaultLabel = ilGen.DefineLabel();
            var lastLable = ilGen.DefineLabel();
            var declare_1 = ilGen.DeclareLocal(typeof(Task));
            foreach (var item in items)
            {
                ilGen.Emit(OpCodes.Ldarg_1);
                ilGen.Emit(OpCodes.Isinst, item.CaseType);
                if (item.Index > 3)
                {
                    ilGen.Emit(OpCodes.Stloc_S, item.DeclareLocal);
                    ilGen.Emit(OpCodes.Ldloc_S, item.DeclareLocal);
                }
                else
                {
                    if (item.Index == 0)
                    {
                        ilGen.Emit(OpCodes.Stloc_0);
                        ilGen.Emit(OpCodes.Ldloc_0);
                    }
                    else if (item.Index == 1)
                    {
                        ilGen.Emit(OpCodes.Stloc_1);
                        ilGen.Emit(OpCodes.Ldloc_1);
                    }
                    else if (item.Index == 2)
                    {
                        ilGen.Emit(OpCodes.Stloc_2);
                        ilGen.Emit(OpCodes.Ldloc_2);
                    }
                    else
                    {
                        ilGen.Emit(OpCodes.Stloc_3);
                        ilGen.Emit(OpCodes.Ldloc_3);
                    }
                }

                ilGen.Emit(OpCodes.Brtrue_S, item.Lable);
            }
            ilGen.Emit(OpCodes.Br_S, defaultLabel);
            foreach (var item in items)
            {
                ilGen.MarkLabel(item.Lable);
                ilGen.Emit(OpCodes.Ldarg_0);
                //加载第一个参数
                if (item.Parameters[0].ParameterType == item.CaseType)
                    LdEventArgs(item, ilGen);
                else if (item.Parameters[0].ParameterType == typeof(EventBase))
                    ilGen.Emit(OpCodes.Ldarg_2);
                //加载第二个参数
                if (item.Parameters.Length == 2)
                {
                    if (item.Parameters[1].ParameterType == item.CaseType)
                        LdEventArgs(item, ilGen);
                    else if (item.Parameters[1].ParameterType == typeof(EventBase))
                        ilGen.Emit(OpCodes.Ldarg_2);
                }
                ilGen.Emit(OpCodes.Call, item.Mehod);
                ilGen.Emit(OpCodes.Stloc_S, declare_1);
                ilGen.Emit(OpCodes.Br_S, lastLable);
            }
            ilGen.MarkLabel(defaultLabel);
            ilGen.Emit(OpCodes.Ldarg_0);
            ilGen.Emit(OpCodes.Ldarg_1);
            ilGen.Emit(OpCodes.Call, GrainType.GetMethod(nameof(DefaultHandler)));
            ilGen.Emit(OpCodes.Stloc_S, declare_1);
            ilGen.Emit(OpCodes.Br_S, lastLable);
            //last
            ilGen.MarkLabel(lastLable);
            ilGen.Emit(OpCodes.Ldloc_S, declare_1);
            ilGen.Emit(OpCodes.Ret);
            handlerInvokeFunc = (Func<object, IEvent, EventBase, Task>)dynamicMethod.CreateDelegate(typeof(Func<object, IEvent, EventBase, Task>));
            //加载Event参数
            void LdEventArgs(SwitchMethodEmit item, ILGenerator gen)
            {
                if (item.Index > 3)
                {
                    gen.Emit(OpCodes.Ldloc_S, item.DeclareLocal);
                }
                else
                {
                    if (item.Index == 0)
                    {
                        gen.Emit(OpCodes.Ldloc_0);
                    }
                    else if (item.Index == 1)
                    {
                        gen.Emit(OpCodes.Ldloc_1);
                    }
                    else if (item.Index == 2)
                    {
                        gen.Emit(OpCodes.Ldloc_2);
                    }
                    else
                    {
                        gen.Emit(OpCodes.Ldloc_3);
                    }
                }
            }
        }

        protected new IGrainFactory GrainFactory { get; private set; }
        private PrimaryKey _GrainId;
        private bool _GrainIdAcquired = false;
        public PrimaryKey GrainId
        {
            get
            {
                if (!_GrainIdAcquired)
                {
                    var type = typeof(PrimaryKey);
                    if (type == typeof(long) && this.GetPrimaryKeyLong() is PrimaryKey longKey)
                        _GrainId = longKey;
                    else if (type == typeof(string) && this.GetPrimaryKeyString() is PrimaryKey stringKey)
                        _GrainId = stringKey;
                    else if (type == typeof(Guid) && this.GetPrimaryKey() is PrimaryKey guidKey)
                        _GrainId = guidKey;
                    else
                        throw new ArgumentOutOfRangeException(typeof(PrimaryKey).FullName);
                    _GrainIdAcquired = true;
                }
                return _GrainId;
            }
        }
        protected CoreOptions ConfigOptions { get; private set; }
        protected ILogger Logger { get; private set; }
        protected ISerializer Serializer { get; private set; }
        /// <summary>
        /// Memory state, restored by snapshot + Event play or replay
        /// </summary>
        protected ObserverSnapshot<PrimaryKey> Snapshot { get; set; }
        /// <summary>
        /// 是否需要保存快照
        /// </summary>
        protected virtual bool SaveSnapshot => true;
        /// <summary>
        /// 是否全量激活，true代表启动时会执行大于快照版本的所有事件,false代表更快的启动，后续有事件进入的时候再处理大于快照版本的事件
        /// </summary>
        protected virtual bool FullyActive => false;
        /// <summary>
        /// 快照的事件版本号
        /// </summary>
        protected long SnapshotEventVersion { get; set; }
        /// <summary>
        /// 是否开启事件并发处理
        /// </summary>
        protected virtual bool ConcurrentHandle => false;
        /// <summary>
        /// Grain的Type
        /// </summary>
        protected Type GrainType { get; }
        /// <summary>
        /// 事件存储器
        /// </summary>
        protected IEventStorage<PrimaryKey> EventStorage { get; private set; }
        /// <summary>
        /// 状态存储器
        /// </summary>
        protected IObserverSnapshotStorage<PrimaryKey> ObserverSnapshotStorage { get; private set; }
        #region 初始化数据
        /// <summary>
        /// 依赖注入统一方法
        /// </summary>
        protected async virtual ValueTask DependencyInjection()
        {
            ConfigOptions = ServiceProvider.GetOptionsByName<CoreOptions>(typeof(MainGrain).FullName);
            Serializer = ServiceProvider.GetService<ISerializer>();
            GrainFactory = ServiceProvider.GetService<IGrainFactory>();
            Logger = (ILogger)ServiceProvider.GetService(typeof(ILogger<>).MakeGenericType(GrainType));
            var configureBuilder = ServiceProvider.GetService<IConfigureBuilder<PrimaryKey, MainGrain>>();
            var storageConfigTask = configureBuilder.GetConfig(ServiceProvider, GrainId);
            if (!storageConfigTask.IsCompletedSuccessfully)
                await storageConfigTask;
            var storageFactory = ServiceProvider.GetService(configureBuilder.StorageFactory) as IStorageFactory;
            //创建事件存储器
            var eventStorageTask = storageFactory.CreateEventStorage(storageConfigTask.Result, GrainId);
            if (!eventStorageTask.IsCompletedSuccessfully)
                await eventStorageTask;
            EventStorage = eventStorageTask.Result;
            //创建状态存储器
            var followConfigTask = configureBuilder.GetObserverConfig(ServiceProvider, GrainType, GrainId);
            if (!followConfigTask.IsCompletedSuccessfully)
                await followConfigTask;
            var stateStorageTask = storageFactory.CreateObserverSnapshotStorage(followConfigTask.Result, GrainId);
            if (!stateStorageTask.IsCompletedSuccessfully)
                await stateStorageTask;
            ObserverSnapshotStorage = stateStorageTask.Result;
        }
        public override async Task OnActivateAsync()
        {
            var dITask = DependencyInjection();
            if (Logger.IsEnabled(LogLevel.Trace))
                Logger.LogTrace("Start activation followgrain with id = {0}", GrainId.ToString());
            if (!dITask.IsCompletedSuccessfully)
                await dITask;
            try
            {
                await ReadSnapshotAsync();
                if (FullyActive)
                {
                    await FullActive();
                }
                if (Logger.IsEnabled(LogLevel.Trace))
                    Logger.LogTrace("Followgrain activation completed with id = {0}", GrainId.ToString());
            }
            catch (Exception ex)
            {
                Logger.LogCritical(ex, "Followgrain activation failed with Id = {0}", GrainId.ToString());
                throw;
            }
        }
        private async Task FullActive()
        {
            while (true)
            {
                var eventList = await EventStorage.GetList(GrainId, Snapshot.StartTimestamp, Snapshot.Version + 1, Snapshot.Version + ConfigOptions.NumberOfEventsPerRead);
                if (ConcurrentHandle)
                {
                    await Task.WhenAll(eventList.Select(@event =>
                    {
                        var task = EventDelivered(@event);
                        if (!task.IsCompletedSuccessfully)
                            return task.AsTask();
                        else
                            return Task.CompletedTask;
                    }));
                    var lastEvt = eventList.Last();
                    Snapshot.UnsafeUpdateVersion(lastEvt.Base);
                }
                else
                {
                    foreach (var @event in eventList)
                    {
                        Snapshot.IncrementDoingVersion(GrainType);//标记将要处理的Version
                        var task = EventDelivered(@event);
                        if (!task.IsCompletedSuccessfully)
                            await task;
                        Snapshot.UpdateVersion(@event.Base, GrainType);//更新处理完成的Version
                    }
                }
                var saveTask = SaveSnapshotAsync();
                if (!saveTask.IsCompletedSuccessfully)
                    await saveTask;
                if (eventList.Count < ConfigOptions.NumberOfEventsPerRead) break;
            };
        }
        public override Task OnDeactivateAsync()
        {
            var needSaveSnap = Snapshot.Version - SnapshotEventVersion >= 1;
            if (Logger.IsEnabled(LogLevel.Information))
                Logger.LogInformation("Followgrain start deactivation with id = {0} ,{1}", GrainId.ToString(), needSaveSnap ? "updated snapshot" : "no update snapshot");
            if (needSaveSnap)
                return SaveSnapshotAsync(true).AsTask();
            else
                return Task.CompletedTask;
        }
        protected virtual async Task ReadSnapshotAsync()
        {
            if (Logger.IsEnabled(LogLevel.Trace))
                Logger.LogTrace("Start read snapshot  with Id = {0}", GrainId.ToString());
            try
            {
                Snapshot = await ObserverSnapshotStorage.Get(GrainId);
                if (Snapshot == null)
                {
                    var createTask = InitFirstSnapshot();
                    if (!createTask.IsCompletedSuccessfully)
                        await createTask;
                }
                SnapshotEventVersion = Snapshot.Version;
                if (Logger.IsEnabled(LogLevel.Trace))
                    Logger.LogTrace("The snapshot of id = {0} read completed, state version = {1}", GrainId.ToString(), Snapshot.Version);
            }
            catch (Exception ex)
            {
                Logger.LogCritical(ex, "The snapshot of id = {0} read failed", GrainId.ToString());
                throw;
            }
        }
        /// <summary>
        /// 初始化状态，必须实现
        /// </summary>
        /// <returns></returns>
        protected virtual ValueTask InitFirstSnapshot()
        {
            Snapshot = new ObserverSnapshot<PrimaryKey>
            {
                StateId = GrainId
            };
            return Consts.ValueTaskDone;
        }
        #endregion
        public Task OnNext(Immutable<byte[]> bytes)
        {
            var (success, transport) = EventBytesTransport.FromBytesWithNoId(bytes.Value);
            if (success)
            {
                var data = Serializer.Deserialize(TypeContainer.GetType(transport.EventTypeCode), transport.EventBytes);
                if (data is IEvent @event)
                {
                    var eventBase = EventBase.FromBytes(transport.BaseBytes);
                    if (eventBase.Version > Snapshot.Version)
                    {
                        var tellTask = Tell(new FullyEvent<PrimaryKey>
                        {
                            StateId = GrainId,
                            Base = eventBase,
                            Event = @event
                        });
                        if (!tellTask.IsCompletedSuccessfully)
                            return tellTask.AsTask();
                    }
                }
                else
                {
                    if (Logger.IsEnabled(LogLevel.Information))
                        Logger.LogInformation("Receive non-event messages, grain Id = {0} ,message type = {1}", GrainId.ToString(), transport.EventTypeCode);
                }
            }
            return Task.CompletedTask;
        }
        public Task<long> GetVersion()
        {
            return Task.FromResult(Snapshot.Version);
        }
        public async Task<long> GetAndSaveVersion(long compareVersion)
        {
            if (SnapshotEventVersion < compareVersion && Snapshot.Version >= compareVersion)
            {
                await SaveSnapshotAsync(true);
            }
            return Snapshot.Version;
        }
        protected async ValueTask Tell(IFullyEvent<PrimaryKey> fullyEvent)
        {
            if (Logger.IsEnabled(LogLevel.Trace))
                Logger.LogTrace("Start event handling, grain Id = {0} and state version = {1},event type = {2} ,event = {3}", GrainId.ToString(), Snapshot.Version, fullyEvent.GetType().FullName, Serializer.SerializeToString(fullyEvent));
            try
            {
                if (fullyEvent.Base.Version == Snapshot.Version + 1)
                {
                    var task = EventDelivered(fullyEvent);
                    if (!task.IsCompletedSuccessfully)
                        await task;
                    Snapshot.FullUpdateVersion(fullyEvent.Base, GrainType);//更新处理完成的Version
                }
                else if (fullyEvent.Base.Version > Snapshot.Version)
                {
                    var eventList = await EventStorage.GetList(GrainId, Snapshot.StartTimestamp, Snapshot.Version + 1, fullyEvent.Base.Version - 1);
                    foreach (var evt in eventList)
                    {
                        var task = EventDelivered(evt);
                        if (!task.IsCompletedSuccessfully)
                            await task;
                        Snapshot.FullUpdateVersion(evt.Base, GrainType);//更新处理完成的Version
                    }
                }
                if (fullyEvent.Base.Version == Snapshot.Version + 1)
                {
                    var task = EventDelivered(fullyEvent);
                    if (!task.IsCompletedSuccessfully)
                        await task;
                    Snapshot.FullUpdateVersion(fullyEvent.Base, GrainType);//更新处理完成的Version
                }
                if (fullyEvent.Base.Version > Snapshot.Version)
                {
                    throw new EventVersionUnorderedException(GrainId.ToString(), GrainType, fullyEvent.Base.Version, Snapshot.Version);
                }
                await SaveSnapshotAsync();
                if (Logger.IsEnabled(LogLevel.Trace))
                    Logger.LogTrace("Event Handling Completion, grain Id ={0} and state version = {1},event type = {2}", GrainId.ToString(), Snapshot.Version, fullyEvent.GetType().FullName);
            }
            catch (Exception ex)
            {
                Logger.LogCritical(ex, "FollowGrain Event handling failed with Id = {0},event = {1}", GrainId.ToString(), Serializer.SerializeToString(fullyEvent));
                throw;
            }
        }
        protected virtual async ValueTask EventDelivered(IFullyEvent<PrimaryKey> fullyEvent)
        {
            if (SnapshotEventVersion > 0 &&
                Snapshot.Version > 0 &&
                fullyEvent.Base.Timestamp < Snapshot.StartTimestamp)
            {
                await ObserverSnapshotStorage.UpdateStartTimestamp(Snapshot.StateId, fullyEvent.Base.Timestamp);
            }
            var task = OnEventDelivered(fullyEvent);
            if (!task.IsCompletedSuccessfully)
                await task;
        }
        public Task DefaultHandler(IEvent evt)
        {
            if (handlerAttribute == default || !handlerAttribute.Ignores.Contains(evt.GetType()))
            {
                throw new EventNotFoundHandlerException(evt.GetType());
            }
            return Task.CompletedTask;
        }
        protected virtual ValueTask OnEventDelivered(IFullyEvent<PrimaryKey> fullyEvent)
        {
            return new ValueTask(handlerInvokeFunc(this, fullyEvent.Event, fullyEvent.Base));
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual ValueTask OnSaveSnapshot() => Consts.ValueTaskDone;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual ValueTask OnSavedSnapshot() => new ValueTask();
        protected virtual async ValueTask SaveSnapshotAsync(bool force = false)
        {
            if (SaveSnapshot)
            {
                if (force || (Snapshot.Version - SnapshotEventVersion >= ConfigOptions.ObserverSnapshotVersionInterval))
                {
                    if (Logger.IsEnabled(LogLevel.Trace))
                        Logger.LogTrace("Start saving state snapshots with Id = {0} ,state version = {1}", GrainId.ToString(), Snapshot.Version);
                    try
                    {
                        var onSaveSnapshotTask = OnSaveSnapshot();//自定义保存项
                        if (!onSaveSnapshotTask.IsCompletedSuccessfully)
                            await onSaveSnapshotTask;
                        if (SnapshotEventVersion == 0)
                        {
                            await ObserverSnapshotStorage.Insert(Snapshot);
                        }
                        else
                        {
                            await ObserverSnapshotStorage.Update(Snapshot);
                        }
                        SnapshotEventVersion = Snapshot.Version;
                        var onSavedSnapshotTask = OnSavedSnapshot();
                        if (!onSavedSnapshotTask.IsCompletedSuccessfully)
                            await onSavedSnapshotTask;
                        if (Logger.IsEnabled(LogLevel.Trace))
                            Logger.LogTrace("State snapshot saved successfully with Id {0} ,state version = {1}", GrainId.ToString(), Snapshot.Version);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "State snapshot save failed with Id = {0}", GrainId.ToString());
                        throw;
                    }
                }
            }
        }
    }
}
