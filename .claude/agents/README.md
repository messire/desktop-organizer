# Субагенты DesktopOrganizer

Набор автономных суб-агентов Claude Code для движения к паритету со **Stardock Fences**.
Создан по шаблонам [AGENTS_ARCHITECTURE.md](AGENTS_ARCHITECTURE.md) и
[AGENT_INTERACTION_PATTERN.md](AGENT_INTERACTION_PATTERN.md) на основе
[../../docs/REFACTORING_PLAN.md](../../docs/REFACTORING_PLAN.md).

Каждый агент подключает профильный скилл из `.claude/skills/` и следует паттерну
`CLARIFICATION_NEEDED` (не спрашивает пользователя напрямую — формирует вопрос основному агенту).

## Состав

| Агент | Роль | Скилл | Модель |
|---|---|---|---|
| `fences-feature-planner` | Декомпозиция фич в планы (DoD, scope, риски) | architecture-designer | opus |
| `win32-interop-specialist` | Нативный слой: WorkerW, hooks, shell, P/Invoke | csharp-developer | opus |
| `wpf-ui-builder` | WPF/XAML/MVVM, поведение панелей | wpf | sonnet |
| `dotnet-services-engineer` | Сервисы/Core/.NET, DI, persistence | dotnet-core-expert | sonnet |
| `tdd-test-engineer` | Тесты xUnit/Moq по TDD | test-driven-development | sonnet |
| `debug-healer` | Диагностика багов от симптома к первопричине | systematic-debugging | opus |
| `code-quality-reviewer` | Ревью кода/ресурсов/безопасности | code-reviewer | opus |
| `fences-ux-parity-reviewer` | Поведенческий паритет с Fences | ux-designer | sonnet |
| `architecture-documenter` | ADR/ROADMAP/план в актуальном виде | architecture-designer | sonnet |

## Конвейер по умолчанию

```
fences-feature-planner
  → (win32-interop-specialist ∥ wpf-ui-builder ∥ dotnet-services-engineer)
    → tdd-test-engineer
      → [debug-healer — при падениях]
        → (code-quality-reviewer ∥ fences-ux-parity-reviewer)   # параллельно!
          → architecture-documenter
```

Два ревьюера запускаются **параллельно одним сообщением** (последовательно — анти-паттерн, создаёт цикл).

## Как запускать

**1. Явно через Task tool (основной агент / Claude Code):**
```
Task tool, subagent_type: "fences-feature-planner",
prompt: "Спланируй Folder Portals (ROADMAP Фаза 5): FileSystemWatcher, debounce, обработка rename/delete."
```

**2. В чате достаточно попросить по смыслу**, например:
- «Спланируй мультимонитор-поддержку» → fences-feature-planner
- «Реализуй FileSystemWatcher для папки-портала» → dotnet-services-engineer
- «Сборка красная, почини» → debug-healer
- «Сделай ревью последних изменений» → code-quality-reviewer (+ ux-parity параллельно)

**3. Автоцепочка:** каждый агент в конце выдаёт `Recommended Next Actions` (CRITICAL/RECOMMENDED/OPTIONAL) — основной агент вызывает следующего по рекомендации.

> Примечание: агенты подхватываются Claude Code из `.claude/agents/`. После добавления новых файлов
> может потребоваться перезапуск сессии Claude Code, чтобы они появились в списке `subagent_type`.
