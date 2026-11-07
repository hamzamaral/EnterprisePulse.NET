using EnterprisePulse.Core.Modules.Assets;

namespace EnterprisePulse.Core.Modules.Sales;

public sealed class SalesQuotesHook() : ManagedRecordHook("sales-quotes", "Teklifler Satış Teklifi ekranından yönetilir");
public sealed class SalesQuoteLinesHook() : ManagedRecordHook("sales-quote-lines", "Teklif satırları teklif formundan yönetilir");
public sealed class SalesOrdersHook() : ManagedRecordHook("sales-orders", "Siparişler Satış Siparişi ekranından yönetilir");
public sealed class SalesOrderLinesHook() : ManagedRecordHook("sales-order-lines", "Sipariş satırları sipariş formundan yönetilir");
public sealed class StockReservationsHook() : ManagedRecordHook("stock-reservations", "Stok rezervasyonu sipariş onayıyla oluşur");
public sealed class DeliveryNotesHook() : ManagedRecordHook("delivery-notes", "İrsaliyeler sevkiyat ekranından kesilir");
public sealed class DeliveryNoteLinesHook() : ManagedRecordHook("delivery-note-lines", "İrsaliye satırları sevkiyat ekranından yönetilir");
public sealed class SalesInvoicesHook() : ManagedRecordHook("sales-invoices", "Satış faturaları fatura ekranından kesilir");
public sealed class SalesInvoiceLinesHook() : ManagedRecordHook("sales-invoice-lines", "Fatura satırları fatura formundan yönetilir");
public sealed class SalesReturnsHook() : ManagedRecordHook("sales-returns", "İadeler satış iadesi ekranından kaydedilir");
public sealed class SalesReturnLinesHook() : ManagedRecordHook("sales-return-lines", "İade satırları iade formundan yönetilir");
