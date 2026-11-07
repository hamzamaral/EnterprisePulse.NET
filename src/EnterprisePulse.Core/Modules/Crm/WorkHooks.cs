using EnterprisePulse.Core.Modules.Assets;

namespace EnterprisePulse.Core.Modules.Crm;

public sealed class LeadsHook() : ManagedRecordHook("leads", "Potansiyel müşteriler CRM ekranından yönetilir");
public sealed class OpportunitiesHook() : ManagedRecordHook("opportunities", "Fırsatlar CRM ekranından yönetilir");
public sealed class CrmActivitiesHook() : ManagedRecordHook("crm-activities", "Aktiviteler CRM ekranından yönetilir");

public sealed class ExpenseReportsHook() : ManagedRecordHook("expense-reports", "Masraf formları masraf ekranından yönetilir");
public sealed class ExpenseLinesHook() : ManagedRecordHook("expense-lines", "Masraf satırları formdan yönetilir");

public sealed class ProjectsHook() : ManagedRecordHook("projects", "Projeler proje ekranından yönetilir");
public sealed class ProjectMembersHook() : ManagedRecordHook("project-members", "Proje üyeleri proje ekranından yönetilir");
public sealed class ProjectTasksHook() : ManagedRecordHook("project-tasks", "Görevler proje ekranından yönetilir");
public sealed class TimeEntriesHook() : ManagedRecordHook("time-entries", "Süre kayıtları proje ekranından girilir");
