using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Trade;

namespace EnterprisePulse.Core.Modules.Finance;

/// <summary>paidAmount covers collections and sales returns. Payment status follows the open remainder.</summary>
public static class InvoicePayments
{
    public static void Apply(SalesInvoice invoice, decimal delta)
    {
        invoice.PaidAmount = DocumentMath.Round2(invoice.PaidAmount + delta);
        Refresh(invoice.Status, invoice.PaidAmount, invoice.GrandTotal, s => invoice.PaymentStatus = s, v => invoice.PaidAmount = v);
    }

    public static void Apply(PurchaseInvoice invoice, decimal delta)
    {
        invoice.PaidAmount = DocumentMath.Round2(invoice.PaidAmount + delta);
        Refresh(invoice.Status, invoice.PaidAmount, invoice.GrandTotal, s => invoice.PaymentStatus = s, v => invoice.PaidAmount = v);
    }

    private static void Refresh(InvoiceStatus status, decimal paid, decimal grand, Action<PaymentStatus> setStatus, Action<decimal> setPaid)
    {
        if (status != InvoiceStatus.ISSUED) return;
        if (paid <= 0)
        {
            setPaid(0);
            setStatus(PaymentStatus.UNPAID);
        }
        else if (paid + 0.004m >= grand) setStatus(PaymentStatus.PAID);
        else setStatus(PaymentStatus.PARTIALLY_PAID);
    }
}
