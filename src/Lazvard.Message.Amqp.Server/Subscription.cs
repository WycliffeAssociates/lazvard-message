﻿using Lazvard.Message.Amqp.Server.Helpers;
using Microsoft.Azure.Amqp;
using Microsoft.Extensions.Logging;

namespace Lazvard.Message.Amqp.Server;

public sealed class Subscription : SubscriptionBase
{
    public Subscription(
        TopicSubscriptionConfig config,
        IMessageQueue messageQueue,
        ConsumerFactory consumerFactory,
        ILoggerFactory loggerFactory,
        CancellationToken stopToken)
        : base(config, messageQueue, consumerFactory, loggerFactory, stopToken)
    {
    }

    private IEnumerable<Consumer> GetActiveConsumers()
    {
        // sorting based on received messages in order to distribute messages among all consumers equally
        return consumers.Values
            .Where(x => !x.IsDrain)
            .OrderBy(x => x.ReceivedMessages);
    }

    protected override void ProcessIncomingMessage(AmqpMessage message, CancellationToken stopToken)
    {
        logger.LogTrace("process message {MessageSeqNo} in subscription {Subscription}",
            message.GetTraceId(), Name);

        if (config.CorrelationFilters.Any())
        {
            foreach(var filter in config.CorrelationFilters)
            {
                if (filter.IsSystem)
                {
                    continue;
                }
                if (!message.GetAppProperties(filter.PropertyName, out var value) || value != filter.PropertyValue)
                {
                    logger.LogTrace("message {MessageSeqNo} in subscription {Subscription} is filtered out",
                        message.GetTraceId(), Name);
                    return;
                }
            }
        }

        var delivered = false;

        var activeConsumers = GetActiveConsumers();
        foreach (var consumer in activeConsumers)
        {
            delivered = consumer.TryToDeliver(message);

            logger.LogTrace("delivering message {MessageSeqNo} in subscription {Subscription} to consumer {Link} was {Status}",
             message.GetTraceId(), Name, "", delivered ? "Successful" : "Failed");

            if (delivered)
                break;
        }

        if (!delivered)
        {
            if (message.Header.DeliveryCount >= config.MaxDeliveryCount)
            {
                logger.LogError("the message {MessageSeqNo} in subscription {Subscription} has reached the maximum delivery count {MaxDeliveryCount} and will be dead-lettered",
                    message.GetTraceId(), Name, config.MaxDeliveryCount);

                if (!messageQueue.TryDeadletter(message))
                {
                    logger.LogError("can not move message {MessageSeqNo} in subscription {Subscription} to dead-lettered",
                        message.GetTraceId(), Name);
                }
            }

            // try again to send the message
            if (!messageQueue.TryReEnqueue(message))
            {
                logger.LogError("can not re-enqueue message {MessageSeqNo} in subscription {Subscription}",
                    message.GetTraceId(), Name);
            }
        }
    }
}
