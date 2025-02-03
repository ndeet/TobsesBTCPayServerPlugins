﻿using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.GhostPlugin.Data;
using BTCPayServer.Services.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.GhostPlugin.Helper;
using BTCPayServer.Plugins.GhostPlugin.ViewModels.Models;
using System.Collections.Generic;
using BTCPayServer.Services.PaymentRequests;
using Newtonsoft.Json.Linq;
using Microsoft.AspNetCore.Routing;
using BTCPayServer.Services.Apps;
using AngleSharp.Dom;
using NBitpayClient;

namespace BTCPayServer.Plugins.GhostPlugin.Services;

public class GhostHostedService : EventHostedServiceBase
{
    private readonly AppService _appService;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GhostDbContextFactory _dbContextFactory;

    public const string PaymentRequestSubscriptionIdKey = "ghostsubscriptionId";
    public const string MemberIdKey = "memberId";
    public const string PaymentRequestSourceKey = "source";
    public const string PaymentRequestSourceValue = "subscription";
    public const string PaymentRequestAppId = "appId";

    public GhostHostedService(AppService appService,
        EventAggregator eventAggregator,
        InvoiceRepository invoiceRepository,
        IHttpClientFactory httpClientFactory,
        GhostDbContextFactory dbContextFactory,
        Logs logs) : base(eventAggregator, logs)
    {
        _appService = appService;
        _dbContextFactory = dbContextFactory;
        _invoiceRepository = invoiceRepository;
        _httpClientFactory = httpClientFactory;
    }

    protected override void SubscribeToEvents()
    {
        Subscribe<InvoiceEvent>();
        base.SubscribeToEvents();
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        switch (evt)
        {
            case InvoiceEvent invoiceEvent when new[]
            {
            InvoiceEvent.MarkedCompleted,
            InvoiceEvent.MarkedInvalid,
            InvoiceEvent.Expired,
            InvoiceEvent.Confirmed,
            InvoiceEvent.Completed
        }.Contains(invoiceEvent.Name):
                {
                    var invoice = invoiceEvent.Invoice;
                    var ghostOrderId = invoice.GetInternalTags(GhostApp.GHOST_MEMBER_ID_PREFIX).FirstOrDefault();
                    if (ghostOrderId != null)
                    {
                        string invoiceStatus = invoice.Status.ToString().ToLower();
                        bool? success = invoiceStatus switch
                        {
                            _ when new[] { "complete", "confirmed", "paid", "settled" }.Contains(invoiceStatus) => true,
                            _ when new[] { "invalid", "expired" }.Contains(invoiceStatus) => false,
                            _ => (bool?)null
                        };
                        if (success.HasValue)
                            await RegisterTransaction(invoice, ghostOrderId, success.Value);
                    }
                    break;
                }

            case PaymentRequestEvent { Type: PaymentRequestEvent.StatusChanged } paymentRequestStatusUpdated:
                {
                    var prBlob = paymentRequestStatusUpdated.Data.GetBlob();
                    if (!prBlob.AdditionalData.TryGetValue(PaymentRequestSourceKey, out var src) ||
                        src.Value<string>() != GhostApp.AppName ||
                        !prBlob.AdditionalData.TryGetValue(PaymentRequestAppId, out var subscriptionAppidToken) ||
                        subscriptionAppidToken.Value<string>() is not { } subscriptionAppId)
                    {
                        return;
                    }

                    var isNew = !prBlob.AdditionalData.TryGetValue(PaymentRequestSubscriptionIdKey, out var subscriptionIdToken);

                    prBlob.AdditionalData.TryGetValue(MemberIdKey, out var memberIdToken);

                    if (isNew && paymentRequestStatusUpdated.Data.Status !=
                        Client.Models.PaymentRequestData.PaymentRequestStatus.Completed)
                    {
                        return;
                    }

                    if (paymentRequestStatusUpdated.Data.Status == Client.Models.PaymentRequestData.PaymentRequestStatus.Completed)
                    {
                        var memberId = memberIdToken?.Value<string>();
                        var blob = paymentRequestStatusUpdated.Data.GetBlob();
                        var memberEmail = blob.Email;

                        await HandlePaidMembershipSubscription(subscriptionAppId, memberId, paymentRequestStatusUpdated.Data.Id, memberEmail);
                    }
                    /*else if (!isNew)
                    {
                        await HandleUnSettledSubscription(subscriptionAppId, subscriptionIdToken.Value<string>(),
                            paymentRequestStatusUpdated.Data.Id);


                    }*/
                    // Add your additional logic here
                    break;
                }
        }

        await base.ProcessEvent(evt, cancellationToken);
    }


    private async Task RegisterTransaction(InvoiceEntity invoice, string shopifyOrderId, bool success)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var ghostSetting = ctx.GhostSettings.AsNoTracking().FirstOrDefault(c => c.StoreId == invoice.StoreId);

        if (ghostSetting.CredentialsPopulated())
        {
            var result = new InvoiceLogs();

            result.Write($"Invoice status: {invoice.Status.ToString().ToLower()}", InvoiceEventData.EventSeverity.Info);
            var transaction = ctx.GhostTransactions.AsNoTracking().FirstOrDefault(c => c.StoreId == invoice.StoreId && c.InvoiceId == invoice.Id && c.TransactionStatus == TransactionStatus.Pending);
            if (transaction == null)
            {
                result.Write("Couldn't find a corresponding Ghost transaction table record", InvoiceEventData.EventSeverity.Error);
                await _invoiceRepository.AddInvoiceLogs(invoice.Id, result);
                return;
            }
            transaction.InvoiceStatus = invoice.Status.ToString().ToLower();
            transaction.TransactionStatus = success ? TransactionStatus.Success : TransactionStatus.Failed;
            if (success)
            {
                try
                {
                    var ghostMember = ctx.GhostMembers.AsNoTracking().FirstOrDefault(c => c.Id == transaction.MemberId);
                    var expirationDate = ghostMember.Frequency == TierSubscriptionFrequency.Monthly ? DateTime.UtcNow.AddMonths(1) : DateTime.UtcNow.AddYears(1);
                    var client = new GhostAdminApiClient(_httpClientFactory, ghostSetting.CreateGhsotApiCredentials());
                    var response = await client.CreateGhostMember(new CreateGhostMemberRequest
                    {
                        members = new List<Member>
                        {
                            new Member
                            {
                                email = ghostMember.Email,
                                name = ghostMember.Name,
                                comped = false,
                                tiers = new List<MemberTier>
                                {
                                    new MemberTier
                                    {
                                        id = ghostMember.TierId,
                                        expiry_at = expirationDate
                                    }
                                }
                            }
                        }
                    });
                    transaction.PeriodStart = DateTime.UtcNow;
                    transaction.PeriodEnd = expirationDate;
                    ghostMember.MemberId = response.members[0].id;
                    ghostMember.MemberUuid = response.members[0].uuid;
                    ghostMember.UnsubscribeUrl = response.members[0].unsubscribe_url;
                    ghostMember.MemberId = response.members[0].id;
                    ghostMember.SubscriptionId = response.members[0].subscriptions.First().id;
                    ghostMember.Status = GhostSubscriptionStatus.New;
                    ctx.UpdateRange(ghostMember);
                    result.Write($"Successfully created member with name: {ghostMember.Name} on Ghost.", InvoiceEventData.EventSeverity.Info);
                }
                catch (Exception ex)
                {
                    Logs.PayServer.LogError(ex,
                        $"Shopify error while trying to create member on Ghost platfor. " +
                        $"Triggered by invoiceId: {invoice.Id}");
                }

            }
            ctx.UpdateRange(transaction);
            ctx.SaveChanges();
            await _invoiceRepository.AddInvoiceLogs(invoice.Id, result);
        }
    }
    

    private async Task HandlePaidMembershipSubscription(string appId, string memberId, string paymentRequestId, string email)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var app = await _appService.GetApp(appId, GhostApp.AppType, false, true);
        if (app == null)
        {
            return;
        }
        var settings = app.GetSettings<GhostSetting>();
        var start = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);

        if (!settings.Members.TryGetValue(memberId, out var member))
        {
            var end = member.Frequency == TierSubscriptionFrequency.Monthly ? start.AddMonths(1).ToDateTime(TimeOnly.MaxValue) : start.AddYears(1).ToDateTime(TimeOnly.MaxValue);
            var existingPayment = member.GhostTransactions.First(p => p.PaymentRequestId == paymentRequestId);
            if (existingPayment is null)
            {
                GhostTransaction transaction = new GhostTransaction
                {
                    StoreId = member.StoreId,
                    PaymentRequestId = paymentRequestId,
                    MemberId = member.Id,
                    TransactionStatus = TransactionStatus.Pending,
                    TierId = member.TierId,
                    Frequency = member.Frequency,
                    CreatedAt = DateTime.UtcNow,
                    PeriodStart = start.ToDateTime(TimeOnly.MinValue),
                    PeriodEnd = end
                };
                ctx.UpdateRange(transaction);
                member.GhostTransactions.Add(transaction);
            }
            else
            {
                existingPayment.PeriodStart = start.ToDateTime(TimeOnly.MinValue);
                existingPayment.PeriodEnd = end;
                existingPayment.TransactionStatus = TransactionStatus.Success;
                ctx.UpdateRange(existingPayment);
            }
            ctx.SaveChanges();
        }
        app.SetSettings(settings);
        await _appService.UpdateOrCreateApp(app);
    }
}
