﻿using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.GhostPlugin.ViewModels;

public class EventTicketViewModel
{
    public string EventId { get; set; }
    public string EventTitle { get; set; }
    public string StoreId { get; set; }
    public string SearchText { get; set; }
    public List<EventTicketVm> Tickets { get; set; }
}

public class EventTicketVm
{
    public bool HasEmailNotificationBeenSent { get; set; }
    public string Id { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; }
    public string Name { get; set; }
    public string Email { get; set; }
    public string InvoiceId { get; set; }
    public string TicketStatus { get; set; }
    public DateTimeOffset CreatedDate { get; set; }

}