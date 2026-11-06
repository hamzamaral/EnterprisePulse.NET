// Emits C# entities, enums and EF Core model configuration from the parsed Prisma schema.
import { mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { parseSchema, pascal, snake, resourceName } from './prisma.mjs';
import { buildMeta } from './meta.mjs';

const ROOT = new URL('../../', import.meta.url).pathname.replace(/^\/(\w:)/, '$1');
const OUT = join(ROOT, 'src/EnterprisePulse.Core/Data/Generated');
const META_OUT = join(ROOT, 'src/EnterprisePulse.Core/Metadata/metadata.json');
const NS = 'EnterprisePulse.Core.Data';

const schema = parseSchema(join(ROOT, 'tools/codegen/source/schema'));
const { models, enums } = schema;

function clrType(f) {
  let t;
  if (f.kind === 'enum') t = f.type;
  else if (f.type === 'String') t = f.dbType === 'Uuid' ? 'Guid' : 'string';
  else if (f.type === 'Int') t = 'int';
  else if (f.type === 'BigInt') t = 'long';
  else if (f.type === 'Decimal') t = 'decimal';
  else if (f.type === 'Float') t = 'double';
  else if (f.type === 'Boolean') t = 'bool';
  else if (f.type === 'DateTime') t = f.dbType === 'Date' ? 'DateOnly' : 'DateTime';
  else if (f.type === 'Json') t = 'JsonDocument';
  return f.optional ? `${t}?` : t;
}

function initializer(f) {
  const d = f.default;
  if (f.optional && d === undefined) return '';
  if (d === 'uuid()') return ' = Guid.NewGuid();';
  if (d === 'now()') return ' = DateTime.UtcNow;';
  if (f.updatedAt) return ' = DateTime.UtcNow;';
  if (d === 'autoincrement()') return '';
  if (d !== undefined) {
    if (f.kind === 'enum') return ` = ${f.type}.${d};`;
    if (f.type === 'Decimal') return ` = ${d}m;`;
    return ` = ${d};`;
  }
  if (f.type === 'String' && f.dbType !== 'Uuid') return ' = null!;';
  if (f.type === 'Json') return ' = null!;';
  return '';
}

const propName = (model, f) => {
  const p = pascal(f.name);
  return p === model.name ? `${p}Value` : p;
};

function emitModel(model) {
  const names = new Set(model.fields.map((f) => f.name));
  const ifaces = [];
  if (model.fields.some((f) => f.name === 'companyId' && !f.optional && f.dbType === 'Uuid')) ifaces.push('ITenantEntity');
  if (names.has('deletedAt')) ifaces.push('ISoftDelete');
  if (model.fields.some((f) => f.name === 'updatedAt' && f.updatedAt)) ifaces.push('IUpdatedAt');
  if (model.fields.some((f) => f.name === 'id' && f.isId && f.dbType === 'Uuid')) ifaces.push('IEntity');

  const lines = [];
  if (model.doc) lines.push(`/// <summary>${model.doc.replace(/&/g, '&amp;').replace(/</g, '&lt;')}</summary>`);
  lines.push(`public partial class ${model.name}${ifaces.length ? ' : ' + ifaces.join(', ') : ''}`, '{');
  for (const f of model.fields) {
    const p = propName(model, f);
    const json = p !== pascal(f.name) ? `[JsonPropertyName("${f.name}")] ` : '';
    if (f.kind === 'relation') {
      if (f.isList) lines.push(`    [JsonIgnore] public List<${f.type}> ${p} { get; set; } = new();`);
      else lines.push(`    [JsonIgnore] public ${f.type}${f.optional ? '?' : ''} ${p} { get; set; }${f.optional ? '' : ' = null!;'}`);
      continue;
    }
    const hidden = ['passwordHash', 'twoFactorSecret', 'tokenHash'].includes(f.name) ? '[JsonIgnore] ' : '';
    lines.push(`    ${hidden}${json}public ${clrType(f)} ${p} { get; set; }${initializer(f)}`);
  }
  lines.push('}');
  return lines.join('\n');
}

function deleteBehavior(f) {
  const od = f.relation.onDelete;
  if (od === 'Cascade') return 'Cascade';
  if (od === 'SetNull') return 'SetNull';
  if (od === 'NoAction') return 'NoAction';
  if (od === 'Restrict') return 'Restrict';
  return null;
}

function emitConfig(model) {
  const out = [];
  const L = (s) => out.push(`            ${s}`);
  const P = (name) => propName(model, model.fields.find((f) => f.name === name));
  out.push(`        b.Entity<${model.name}>(e =>`, '        {');
  L(`e.ToTable("${model.table ?? snake(model.name)}");`);
  if (model.compositeId) L(`e.HasKey(x => new { ${model.compositeId.map((c) => `x.${P(c)}`).join(', ')} });`);
  else L(`e.HasKey(x => x.${P(model.fields.find((f) => f.isId).name)});`);

  for (const f of model.fields.filter((x) => x.kind !== 'relation')) {
    const p = propName(model, f);
    let cfg = `e.Property(x => x.${p}).HasColumnName("${snake(f.name)}")`;
    if (f.kind === 'enum') cfg += '.HasConversion<string>()';
    if (f.type === 'Decimal') cfg += `.HasPrecision(${(f.dbArgs ?? [18, 4]).join(', ')})`;
    if (f.type === 'Json') cfg += '.HasColumnType("jsonb")';
    if (f.default === 'autoincrement()') cfg += '.UseIdentityByDefaultColumn()';
    if (f.isId && f.dbType === 'Uuid') cfg += '.ValueGeneratedNever()';
    L(cfg + ';');
    if (f.unique) L(`e.HasIndex(x => x.${p}).IsUnique();`);
  }
  for (const u of model.uniques) L(`e.HasIndex(x => new { ${u.map((c) => `x.${P(c)}`).join(', ')} }).IsUnique();`);
  for (const i of model.indexes) L(`e.HasIndex(x => new { ${i.map((c) => `x.${P(c)}`).join(', ')} });`);

  for (const f of model.fields.filter((x) => x.kind === 'relation' && x.owning)) {
    const target = models[f.type];
    const inv = target.fields.find((x) => x.name === f.inverse);
    const fkName = f.relation.fields[0];
    const fkField = model.fields.find((x) => x.name === fkName);
    const fk = P(fkName);
    const nav = propName(model, f);
    const invProp = propName(target, inv);
    let cfg = `e.HasOne(x => x.${nav})`;
    if (inv.isList) cfg += `.WithMany(x => x.${invProp}).HasForeignKey(x => x.${fk})`;
    else cfg += `.WithOne(x => x.${invProp}).HasForeignKey<${model.name}>(x => x.${fk})`;
    const ref = f.relation.references[0];
    const targetPk = target.fields.find((x) => x.isId)?.name;
    if (ref !== targetPk) cfg += inv.isList ? `.HasPrincipalKey(x => x.${propName(target, target.fields.find((x) => x.name === ref))})` : `.HasPrincipalKey<${target.name}>(x => x.${propName(target, target.fields.find((x) => x.name === ref))})`;
    const behavior = deleteBehavior(f) ?? (fkField.optional ? 'SetNull' : 'Restrict');
    cfg += `.IsRequired(${!fkField.optional}).OnDelete(DeleteBehavior.${behavior})`;
    L(cfg + ';');
  }
  out.push('        });');
  return out.join('\n');
}

rmSync(OUT, { recursive: true, force: true });
mkdirSync(OUT, { recursive: true });

const header = '// <auto-generated>Generated by tools/codegen/emit-csharp.mjs. Do not edit.</auto-generated>\n#nullable enable\n';

writeFileSync(
  join(OUT, 'Enums.g.cs'),
  `${header}namespace ${NS};\n\n` +
    Object.values(enums)
      .map((e) => `public enum ${e.name}\n{\n${e.values.map((v) => `    ${v},`).join('\n')}\n}`)
      .join('\n\n') +
    '\n',
);

const byFile = {};
for (const m of Object.values(models)) (byFile[m.file] ??= []).push(m);
for (const [file, list] of Object.entries(byFile)) {
  writeFileSync(
    join(OUT, `Entities.${pascal(file)}.g.cs`),
    `${header}using System.Text.Json;\nusing System.Text.Json.Serialization;\n\nnamespace ${NS};\n\n${list.map(emitModel).join('\n\n')}\n`,
  );
}

const dbSetName = (m) => resourceName(m.name).split('-').map(pascal).join('');
writeFileSync(
  join(OUT, 'AppDbContext.g.cs'),
  `${header}using Microsoft.EntityFrameworkCore;\n\nnamespace ${NS};\n\npublic partial class AppDbContext\n{\n` +
    Object.values(models)
      .map((m) => `    public DbSet<${m.name}> ${dbSetName(m)} => Set<${m.name}>();`)
      .join('\n') +
    `\n\n    private static void ConfigureGenerated(ModelBuilder b)\n    {\n${Object.values(models).map(emitConfig).join('\n')}\n    }\n}\n`,
);

mkdirSync(join(ROOT, 'src/EnterprisePulse.Core/Metadata'), { recursive: true });
writeFileSync(META_OUT, JSON.stringify(buildMeta(schema, join(ROOT, 'tools/codegen/source/locales')), null, 1));
console.log(`Generated ${Object.keys(models).length} entities, ${Object.keys(enums).length} enums.`);
