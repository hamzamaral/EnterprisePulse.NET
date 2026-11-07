using EnterprisePulse.Core.Modules.Assets;

namespace EnterprisePulse.Core.Modules.Finance;

public sealed class TreasuryTransactionsHook() : ManagedRecordHook("treasury-transactions", "Kasa hareketleri tahsilat, ödeme ve çek işlemleriyle oluşur");
public sealed class PartnerPaymentsHook() : ManagedRecordHook("partner-payments", "Tahsilat ve ödemeler finans ekranından kaydedilir");
public sealed class PaymentAllocationsHook() : ManagedRecordHook("payment-allocations", "Mahsuplar tahsilat veya ödeme belgesiyle yazılır");
public sealed class ChequesHook() : ManagedRecordHook("cheques", "Çek ve senetler tahsilat veya ödeme sırasında oluşur");
public sealed class ChequeEventsHook() : ManagedRecordHook("cheque-events", "Çek hareketleri portföy ekranından yazılır");
