﻿using Ray.Core.Message;
using Ray.RabbitMQ;
using System.Threading.Tasks;
using Ray.IGrains;
using System;
using Ray.Core.MQ;
using Ray.Core;
using Ray.IGrains.Actors;
using Ray.Core.EventSourcing;

namespace Ray.Handler
{
    [RabbitSub("Read", "Account", "account")]
    public sealed class AccountToDbHandler : SubHandler<MessageInfo>
    {
        IOrleansClientFactory clientFactory;
        public AccountToDbHandler(IServiceProvider svProvider, IOrleansClientFactory clientFactory) : base(svProvider)
        {
            this.clientFactory = clientFactory;
        }
        public override Task Tell(byte[] bytes, IMessage data, MessageInfo msg)
        {
            if (data is IEventBase<string> evt)
                return clientFactory.CreateClient().GetGrain<IAccountDb>(evt.StateId).Tell(bytes);
            return Task.CompletedTask;
        }
    }
}