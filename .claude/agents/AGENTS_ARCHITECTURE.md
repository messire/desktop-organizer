# 🏗️ Архитектура автономных суб-агентов

Архитектурная документация системы распределённых автономных суб-агентов для Claude Code.

**Основано на анализе:** [../docs/AGENTS_ECOSYSTEM_ANALYSIS.md](../docs/AGENTS_ECOSYSTEM_ANALYSIS.md)

---

## 🎯 Архитектурное видение

### Ключевая проблема
Разработка сложного ПО требует множества последовательных этапов (планирование → валидация → реализация → тестирование → ревью → документирование → коммит). Разрывы между этапами приводят к:
- Пропуску критичных шагов (20-30% случаев)
- Длительным ручным ревью (1.5-2.5 часа)
- Отсутствию метрик качества (LLM Readiness, Coverage)
- Необходимости постоянного участия пользователя

### Архитектурное решение
**Распределённая самоорганизующаяся система автономных агентов** с встроенными рекомендациями переходов.

**НЕ централизованная оркестрация** (workflow-orchestrator + workflows.yaml), **А распределённая координация** через cross-recommendations между агентами.

## 🏛️ Архитектурные принципы

### 1. Распределённая самоорганизация (Distributed Self-Organization)

**Принцип:** Каждый агент самостоятельно определяет, какие агенты должны быть вызваны после его завершения.

**Реализация:**
```markdown
## 🔄 АВТОМАТИЧЕСКИЕ РЕКОМЕНДАЦИИ

### При успешном завершении:
**CRITICAL:**
- **work-plan-reviewer**: Validate plan structure
  - Condition: Always after plan creation
  - Reason: Ensure plan follows standards

**RECOMMENDED:**
- **parallel-plan-optimizer**: Optimize for parallel execution
  - Condition: Plan has >5 tasks
  - Reason: Reduce execution time by 40-50%
```

**Преимущества:**
- ✅ Нет single point of failure (централизованного orchestrator)
- ✅ Агенты эволюционируют независимо
- ✅ Легко добавлять новых агентов
- ✅ Естественная fault tolerance

### 2. Автономное продление работы (Autonomous Work Extension)

**Принцип:** Агенты рекомендуют следующие шаги в формате, который Claude Code может автоматически интерпретировать и выполнять.

**Формат рекомендаций:**
```
✅ [Agent Name] completed successfully

[Результаты работы]

🔄 Recommended Next Actions:

1. 🚨 CRITICAL: [agent-name]
   Reason: [почему обязательно]
   Command: Use Task tool with subagent_type: "[agent-name]"
   Parameters: [key parameters]

2. ⚠️ RECOMMENDED: [agent-name]
   Reason: [почему рекомендуется]
   Command: Use Task tool with subagent_type: "[agent-name]"

3. 💡 OPTIONAL: [agent-name]
   Reason: [когда полезно]
   Condition: [условие активации]
```

**Уровни приоритета:**
- 🚨 **CRITICAL** - обязательные следующие шаги (блокируют workflow без выполнения)
- ⚠️ **RECOMMENDED** - рекомендуемые шаги (улучшают качество)
- 💡 **OPTIONAL** - условные шаги (зависят от контекста)

### 3. Built-in Workflow Patterns

**Принцип:** Workflows встроены в агентов через рекомендации, а не определены в централизованном конфиге.

**Примеры встроенных workflow:**

#### Feature Development Pipeline
```
work-plan-architect → work-plan-reviewer → systematic-plan-reviewer →
plan-readiness-validator → plan-task-executor →
[IF compilation errors: compilation-healer] →
plan-review-iterator → plan-task-completer →
[IF test failures: test-healer] →
code-principles-reviewer (parallel) + code-style-reviewer (parallel) →
architecture-documenter → pre-completion-validator → git-workflow-manager
```

#### Bug Fix Pipeline (TDD)
```
test-healer (create failing test) → plan-task-executor (fix bug) →
[IF compilation errors: compilation-healer] →
plan-review-iterator → plan-task-completer →
test-healer (green tests) → test-healer (regression) →
code-principles-reviewer + code-style-reviewer →
pre-completion-validator → git-workflow-manager
```

#### Refactoring Pipeline
```
work-plan-architect → test-healer (baseline 100%) →
dependency-analyzer → plan-task-executor →
[IF compilation errors: compilation-healer] →
plan-review-iterator → plan-task-completer →
test-healer (incremental) → performance-profiler →
code-principles-reviewer + code-style-reviewer →
architecture-documenter → pre-completion-validator → git-workflow-manager
```

#### Merge Conflict Resolution Pipeline (Deep Branch)
```
git-blame-merge-resolver → codebase-researcher (if architecture unclear) →
work-plan-architect → plan-task-executor → plan-review-iterator →
plan-task-completer → code-principles-reviewer + code-style-reviewer →
pre-completion-validator → git-workflow-manager
```

#### PR Review Pipeline
```
pr-review-coordinator → [batching] →
pr-code-reviewer (batch 1) + pr-code-reviewer (batch 2) + ... →
pr-review-coordinator (consolidate) → gh pr comment
```

#### Two-Phase Planning Pipeline (STAGE 1 → STAGE 2)

**Simple/Medium Tasks (S/M):**
```
codebase-researcher (general) → high-level-plan-architect (L0 analytical) →
high-level-plan-reviewer (validate) → codebase-researcher (per-task, parallel) →
work-plan-architect (STAGE 2, per item) → plan-task-executor
```

**Large Tasks (L):**
```
codebase-researcher (general) → high-level-plan-architect (L0, 2 levels) →
high-level-plan-reviewer (validate nesting, DoD, artifacts) →
codebase-researcher (per-task, 7+ items) →
work-plan-architect (STAGE 2, per L0 item) → plan-task-executor
```

**Extra Large Tasks with Recursion (XL):**
```
codebase-researcher (general) → high-level-plan-architect (L0, XL items flagged) →
high-level-plan-reviewer (validate) →
recursive-plan-coordinator (XL items) →
  ↳ high-level-plan-architect (L1 child analytical) →
    high-level-plan-reviewer (L1 validation) →
    codebase-researcher (L1 per-task) →
    work-plan-architect (L1 STAGE 2, per L1 item) →
    plan-task-executor (L1 tasks) →
    [L1 completion aggregates to L0]
→ plan-task-executor (continue L0 non-XL items)
```

**Late Recursion (M→XL during STAGE 2):**
```
work-plan-architect (STAGE 2) → [detects XL complexity] → STOP →
recursive-plan-coordinator (rollback to STAGE 1 for this item) →
high-level-plan-architect (L1 child for escalated item) →
[... recursive workflow ...]
```

**Реализация:** Каждый агент в цепочке рекомендует следующего через CRITICAL/RECOMMENDED/OPTIONAL.

### 4. Conditional Recommendations

**Принцип:** Рекомендации зависят от контекста выполнения и результатов работы.

**Примеры условий:**

```markdown
### Conditional recommendations:

- IF plan.tasks > 5 THEN recommend **parallel-plan-optimizer**
  - Reason: Large plans benefit from parallel execution

- IF plan.has_architecture_changes THEN recommend **architecture-documenter**
  - Reason: Document architectural decisions

- IF test_failures.contains("DI issues") THEN recommend **code-principles-reviewer**
  - Reason: Validate Dependency Injection architecture

- IF refactoring = true THEN recommend **performance-profiler**
  - Reason: Ensure no performance degradation

- IF deep_branch_merge AND (inconsistent_files OR confidence < 90%) THEN recommend **git-blame-merge-resolver**
  - Reason: Analyze git history to resolve merge conflicts and verify correct inheritance
  - Example: Properties lost during file migration, class divergence, unclear file origins

- IF git-blame-merge-resolver detects bad_merge OR architectural_uncertainty THEN recommend **codebase-researcher**
  - Reason: Understand current architecture state before planning fixes
  - Condition: Always after git blame analysis

- IF (codebase-researcher OR work-plan-architect) confidence < 90% AND complex_merge THEN recommend **git-blame-merge-resolver**
  - Reason: Deep git history analysis to clarify file lineage and architectural decisions
  - Applies to: Both research phase (if uncertain what exists) and planning phase (if uncertain how to resolve)

- IF git-blame-merge-resolver confidence < 70% THEN attempt deeper git analysis (authorship, patterns, timing)
  - Reason: Often more git history reveals the answer before escalating to user
  - Only escalate to user if comprehensive analysis yields no clear resolution
```

### 5. Explicit Tool Requirements

**Принцип:** Каждый агент явно декларирует используемые tools в frontmatter для Claude Code.

**Формат frontmatter:**
```yaml
---
name: systematic-plan-reviewer
description: "Automated plan structure validation using PowerShell scripts"
tools: Bash, Read, Glob, Grep, TodoWrite
model: opus
color: blue
---
```

**Обязательные поля:**
- `name` (kebab-case) - уникальный идентификатор агента
- `description` - краткое описание (1-2 предложения или examples)
- `tools` - inline comma-separated list: `Tool1, Tool2, Tool3`

**Опциональные поля:**
- `model` - opus, sonnet, haiku (предпочтительная модель для агента)
- `color` - цвет агента для визуализации (blue, green, orange, red и т.д.)

**Примечание:** Поля `priority` и `estimated_implementation` были удалены из всех агентов как галлюцинированные (не поддерживаются официальной спецификацией Claude Code)

### 6. Full Path Tracing (обязательно при API-цепочках)

**Принцип:** При исследовании любого API-вызова — codebase-researcher обязан трассировать до конечного endpoint и прочитать его реализацию (валидации, комментарии, типы параметров). Нельзя останавливаться на уровне proxy/client интерфейса.

**Почему:** Proxy/client слои (Gateway clients, HTTP wrappers) часто используют `string` параметры, чья реальная семантика видна только в конечном endpoint (Controller action). Остановка на уровне сигнатуры proxy создаёт ложное понимание контракта.

**Пример:**
```
НЕПРАВИЛЬНО:
  Researcher видит: IGatewayAdminClient.AddUserClaimAsync(userId, request)
  request = UserClaimAssignmentRequest { ClaimType: string, ClaimValue: string }
  Researcher записывает: "ClaimValue — строка, видимо boolean flag"
  → План строится на ложном предположении → 500 error в runtime

ПРАВИЛЬНО:
  Researcher трассирует: Gateway → AuthService ClaimsController.AddUserClaim()
  Видит: Guid.TryParse(request.ClaimValue) + комментарий "database Id GUID"
  Researcher записывает: "ClaimValue MUST be valid GUID (database/project ID)"
  → План корректен с первой итерации
```

**Кто применяет:**
- **codebase-researcher**: обязательный шаг в Этап 3 (Architecture Analysis)
- **work-plan-architect**: при валидации research — проверяет наличие full path tracing для API-вызовов

### 7. CLARIFICATION_NEEDED Pattern для агентов с потерей уверенности

**Принцип:** Когда агент теряет уверенность (confidence < 90%), он НЕ спрашивает пользователя напрямую. Вместо этого он формулирует конкретный вопрос к основному агенту (Claude Code) с вариантами ответов A/B/C.

**Когда применяется:**
- codebase-researcher при неясности о текущей архитектуре (3+ версии компонента)
- work-plan-architect при неясности о технических решениях
- test-healer при обнаружении архитектурных конфликтов в тестах
- code-principles-reviewer при неясной интерпретации принципов
- git-blame-merge-resolver при неясной истории и intent файлов

**Жизненный цикл:**

```
1️⃣ ПОТЕРЯ УВЕРЕННОСТИ (confidence 70-90%):
   - Агент выполняет дополнительный анализ/исследование
   - Пытается восстановить уверенность через deeper analysis
   - Если confidence остаётся < 90%

2️⃣ CLARIFICATION_NEEDED (Intermediate Results):
   - Формирует промежуточные результаты с указанием confidence %
   - Описывает что найдено и что осталось неясным
   - Формулирует конкретный вопрос к основному агенту (НЕ к пользователю)
   - Предлагает 3-4 конкретных варианта ответа (A/B/C/D)

3️⃣ ОСНОВНОЙ АГЕНТ ОБРАБАТЫВАЕТ:
   ✅ Если ответ известен → Re-invoke субагента с ответом
   ⚠️ Если ответ неизвестен → Escalate к пользователю с конкретными вариантами
```

**Пример CLARIFICATION_NEEDED:**

```markdown
⚠️ **INTERMEDIATE RESULTS (Confidence: 72%)**

## Найдено:
- Существует 3 версии AuthenticationService (Core, Shared, Microservices)
- Core версия используется в 12 местах
- Microservices версия содержит новые features

## Что осталось неясным:
- Какая версия является ACTIVE/PRIMARY?
- Должны ли версии быть синхронизированы?

## ❓ CLARIFICATION NEEDED (для основного агента):

Which AuthenticationService should be treated as authoritative?
A) Core (legacy, most usage)
B) Microservices (newer features)
C) Shared (for new projects)
D) All three coexist with different purposes

[ВАЖНО: Основной агент решит ответить сам или спросить пользователя]
```

**Преимущества паттерна:**
- ✅ Агенты не спрашивают пользователя напрямую (меньше interrupt)
- ✅ Основной агент может ответить на 60-70% вопросов без escalation
- ✅ Когда escalation нужен, вопрос уже структурирован для пользователя
- ✅ Агенты могут продолжить после получения ответа (re-invoke с решением)

**Статус реализации:**
- ✅ git-blame-merge-resolver - полная реализация
- ✅ codebase-researcher - добавлен in Phase 1
- ✅ test-healer - добавлен in Phase 1
- ✅ code-principles-reviewer - добавлен in Phase 1
- ✅ work-plan-architect - добавлен in Phase 2
- ✅ high-level-plan-architect - добавлен для two-phase planning (STAGE 1)
- ✅ high-level-plan-reviewer - добавлен для two-phase planning (STAGE 1)
- ✅ recursive-plan-coordinator - добавлен для recursion management

**Документация:** See `.claude/AGENT_INTERACTION_PATTERN.md` for complete specification


## 🔄 Two-Phase Planning System

### Архитектурная проблема

Традиционная система планирования (work-plan-architect напрямую создает детальные планы) приводила к:
- **Context overflow**: Смешение архитектурных решений и деталей реализации в одном плане
- **Глубокая вложенность**: Планы уходят на 3-4+ уровня, теряя обозримость
- **Потеря артефактов**: Общий research не детализирован для каждого пункта
- **Ранняя детализация**: Детальные шаги создаются до понимания high-level структуры

### Решение: STAGE 1 (Analytical) → STAGE 2 (Execution)

**STAGE 1 - High-Level Analytical Planning:**
- Агент: `high-level-plan-architect`
- Цель: Декомпозиция на архитектурные компоненты/фазы
- Ограничения: MAX 2 уровня вложенности **В ПРЕДЕЛАХ ОДНОГО ПЛАНА** (sections → items)
  - ⚠️ **ВАЖНО**: Это НЕ глобальное ограничение на всю иерархию!
  - ⚠️ Если пункт требует 3+ уровней → **СОЗДАТЬ CHILD PLAN** через recursive-plan-coordinator
  - ❌ НЕПРАВИЛЬНО: flatten или удалить вложенность
  - ✅ ПРАВИЛЬНО: новый цикл планирования для крупного пункта
- Output: Аналитический план с DoD, scope (S/M/L/XL), dependencies, risks
- Артефакты: Per-task research для каждого пункта
- Recursion: XL items флагуются для L1 recursion (создание child plan)

**STAGE 2 - Detailed Execution Planning:**
- Агент: `work-plan-architect` (per analytical plan item)
- Цель: Детальные шаги реализации для конкретного пункта STAGE 1
- Input: Analytical plan item + per-task research + parent DoD
- Output: Execution plan (файлы, методы, тесты, конфиг)
- Acceptance Criteria: Mapped from STAGE 1 DoD

### Recursion Management

**Глубина рекурсии:**
- L0 (Root): Главный аналитический план
- L1 (First recursion): Для XL items из L0
- L2 (Second recursion): Для XL items из L1
- L3 (Atomic only): Только atomic tasks (1 file/class/endpoint, <1 day)

**Агент `recursive-plan-coordinator` управляет:**
- Depth enforcement (MAX_DEPTH=3, L3 только atomic)
- Parent-child context passing (DoD, scope, research)
- Completion aggregation (child DoD → parent DoD satisfaction)
- Late Recursion (M→XL transition during STAGE 2)
- Cycle detection (recursion_path tracking, hash-based validation)

**Triggers для рекурсии (Hybrid-approach):**
- **AUTO**: Architectural change, breaking changes → автоматическая рекурсия
- **RECOMMEND**: >3 modules, >7 DoD criteria, greenfield → рекомендация с 85%+ confidence
- **SUBJECTIVE**: Agent assessment на основе research → escalation если <85% confidence

### Workflow Examples

**Small task (S) - Skip STAGE 1:**
```
User request → codebase-researcher → work-plan-architect (STAGE 2 directly) → plan-task-executor
```

**Medium task (M) - Flat STAGE 1:**
```
User request → codebase-researcher (general) →
high-level-plan-architect (L0, 1 level: flat list 5 items) →
high-level-plan-reviewer (approve) →
codebase-researcher (per-task, 5 artifacts) →
work-plan-architect (STAGE 2, per item) → plan-task-executor
```

**Large task (L) - 2-level STAGE 1:**
```
User request → codebase-researcher (general) →
high-level-plan-architect (L0, 2 levels: 3 sections, 9 items) →
high-level-plan-reviewer (approve) →
codebase-researcher (per-task, 9 artifacts) →
work-plan-architect (STAGE 2, per item) → plan-task-executor
```

**XL task with recursion - L0→L1:**
```
User request → codebase-researcher (general) →
high-level-plan-architect (L0, XL item flagged) →
high-level-plan-reviewer (approve) →
recursive-plan-coordinator (for XL item) →
  high-level-plan-architect (L1 child analytical) →
  high-level-plan-reviewer (L1 approve) →
  codebase-researcher (L1 per-task) →
  work-plan-architect (L1 STAGE 2) → plan-task-executor (L1 tasks) →
  [Completion aggregation: L1 DoD → L0 DoD satisfied] →
plan-task-executor (continue L0 non-XL items)
```

**Late Recursion (M→XL):**
```
STAGE 1: Item marked M (1 week)
STAGE 2: work-plan-architect detects XL complexity (5 providers needed) →
         STOP STAGE 2 →
         recursive-plan-coordinator (rollback to STAGE 1 for this item) →
         Update parent: Scope M → XL →
         Create L1 analytical plan →
         [... recursive workflow ...]
```

### Benefits

**Context Management:**
- STAGE 1 focuses on WHAT (architectural decisions, high-level breakdown)
- STAGE 2 focuses on HOW (implementation details, specific files/methods)
- No mixing → clearer plans, less context overflow

**Controlled Nesting:**
- MAX 2 levels в STAGE 1 enforced by high-level-plan-reviewer
- Deep complexity handled via recursion, not inline nesting
- Each recursion level has own 2-level limit

**Artifact Coverage:**
- General research (L0) → Per-task research (per L0 item) → Focused research (if L1 recursion)
- Each planning level has appropriate research context
- No reinventing wheels during execution

**Quality Gates:**
- STAGE 1: DoD quality, nesting limits, artifact coverage, scope validation
- STAGE 2: Acceptance criteria mapping, execution readiness, resource availability
- Recursion: Depth limits, cycle detection, atomic validation

### Metrics Impact

| Metric | Before (Traditional) | After (Two-Phase) | Improvement |
|--------|---------------------|-------------------|-------------|
| Context overflow | 30-40% tasks | <10% tasks | **3-4x** |
| Plan revisions | 2-3 iterations | 1-2 iterations | **30-50%** |
| Nesting violations | 15-20% plans | <5% plans (enforced) | **75%** |
| Artifact coverage | 60-70% items | 100% items (required) | **+40%** |
| Late scope discovery | 25-30% tasks | <10% tasks (research+DoD) | **60-70%** |

### File Structure

**Traditional (work-plan-architect only):**
```
docs/PLANS/
├── feature-plan.md (detailed execution plan, 3-4 levels deep)
└── feature-plan/
    ├── 01-setup.md (execution details)
    └── 02-implementation.md (execution details)
```

**Two-Phase (high-level + work-plan):**
```
docs/PLANS/
├── feature-plan.md (L0 analytical, 2 levels max)
└── feature-plan/
    ├── 00-MASTER-PLAN.md (L0 full analytical plan)
    ├── 01-infrastructure.md (STAGE 2 execution for L0 Item 1)
    ├── 02-RECURSIVE/ (L1 recursion for L0 XL Item 2)
    │   ├── 00-ANALYTICAL-PLAN.md (L1 analytical)
    │   ├── recursion_tracking.json (metadata)
    │   ├── 01-provider-interface.md (L1 STAGE 2)
    │   └── 02-google-adapter.md (L1 STAGE 2)
    └── 03-security.md (STAGE 2 execution for L0 Item 3)
```

### Integration with Existing Agents

**Unchanged agents:**
- plan-task-executor: Executes tasks from STAGE 2 plans (no change)
- plan-review-iterator: Reviews execution results (no change)
- plan-task-completer: Marks tasks complete (no change)
- code reviewers: Validate code quality (no change)

**Enhanced agents:**
- codebase-researcher: Added `per-task` mode for focused per-item research
- work-plan-architect: Added `analytical_plan_item` parameter, reads per-task artifacts
- work-plan-reviewer: Validates DoD mapping to Acceptance Criteria

**New agents:**
- high-level-plan-architect: STAGE 1 analytical planning
- high-level-plan-reviewer: STAGE 1 validation (nesting, DoD, artifacts, scope)
- recursive-plan-coordinator: Manages recursion transitions, depth limits, aggregation


## 📊 Матрица переходов агентов

Таблица показывает CRITICAL и RECOMMENDED переходы между агентами:

### Planning Agents (Traditional STAGE 2)

| Агент | CRITICAL recommendations | RECOMMENDED recommendations |
|-------|--------------------------|----------------------------|
| **work-plan-architect** | work-plan-reviewer<br>architecture-documenter (if arch changes) | parallel-plan-optimizer (>5 tasks)<br>plan-readiness-validator<br>git-blame-merge-resolver (if confidence < 90% on deep branch) |
| **work-plan-reviewer** | work-plan-architect (if violations) | systematic-plan-reviewer<br>architecture-documenter |
| **systematic-plan-reviewer** | work-plan-architect (if critical violations) | plan-readiness-validator<br>architecture-documenter |
| **plan-readiness-validator** | work-plan-architect (if <90% score) | plan-task-executor (if ≥90%) |
| **codebase-researcher** | work-plan-architect (for planning) | git-blame-merge-resolver (if confidence < 90% on file origins or inheritance)<br>architecture-documenter (for research findings) |

### Two-Phase Planning Agents (STAGE 1)

| Агент | CRITICAL recommendations | RECOMMENDED recommendations |
|-------|--------------------------|----------------------------|
| **high-level-plan-architect** | high-level-plan-reviewer (always after creation)<br>codebase-researcher (if no general research) | architecture-documenter (if arch changes)<br>recursive-plan-coordinator (if XL scope) |
| **high-level-plan-reviewer** | high-level-plan-architect (if REQUIRES_REVISION, iteration ≤3) | codebase-researcher (per-task, after approval)<br>work-plan-architect (STAGE 1→2 transition)<br>plan-structure-fixer (if only structural issues) |
| **recursive-plan-coordinator** | high-level-plan-architect (child L{N+1}) | plan-task-executor (resume parent after child completion) |

### Execution & Review Agents

| Агент | CRITICAL recommendations | RECOMMENDED recommendations |
|-------|--------------------------|----------------------------|
| **plan-task-executor** | plan-review-iterator (always after execution) | - |
| **plan-review-iterator** | plan-task-completer (if reviews satisfied 80%+) | Self-iteration (if issues found, max 2) |
| **plan-task-completer** | work-plan-reviewer (always)<br>plan-task-executor (next task) | parallel-plan-optimizer (if ≥3 ready tasks)<br>architecture-documenter (if arch change) |
| **compilation-healer** | pre-completion-validator (if 100% success) | code-principles-reviewer (if arch changes)<br>code-style-reviewer (if code modified) |
| **test-healer** | pre-completion-validator (if 100%) | code-principles-reviewer (if DI issues) |
| **code-principles-reviewer** | - | architecture-documenter (if violations) |
| **code-style-reviewer** | - | - |
| **pr-review-coordinator** | pr-code-reviewer (per batch) | - |
| **pr-code-reviewer** | pr-review-coordinator (results) | - |
| **pre-completion-validator** | work-plan-reviewer (if mismatches)<br>git-workflow-manager (if OK) | - |

### Infrastructure & Utilities

| Агент | CRITICAL recommendations | RECOMMENDED recommendations |
|-------|--------------------------|----------------------------|
| **architecture-documenter** | - | work-plan-architect (if redesign needed) |
| **parallel-plan-optimizer** | plan-task-executor | architecture-documenter |
| **git-workflow-manager** | pre-completion-validator (before commit) | test-healer (if tests not run)<br>compilation-healer (if compilation errors) |
| **git-blame-merge-resolver** | codebase-researcher (always after analysis)<br>work-plan-architect (if findings clear) | - |

## 🔧 Структура агента

### Стандартные секции спецификации

Каждый агент должен содержать следующие секции (в порядке):

```markdown
---
[frontmatter - см. раздел "Explicit Tool Requirements"]
---

# [Agent Name] Agent

## 🎯 НАЗНАЧЕНИЕ
Чёткое определение проблемы и решения.

## 🛠️ ИНСТРУМЕНТЫ
Список tools с пояснением использования.

## 📋 WORKFLOW
Пошаговый алгоритм работы агента.

## 🔄 АВТОМАТИЧЕСКИЕ РЕКОМЕНДАЦИИ
### При успешном завершении:
**CRITICAL:** [обязательные шаги]
**RECOMMENDED:** [рекомендуемые шаги]

### При обнаружении проблем:
**CRITICAL:** [как исправлять]

### Conditional recommendations:
IF-THEN правила условных рекомендаций

### Example output:
Пример финального вывода с рекомендациями

## 📊 МЕТРИКИ УСПЕХА
Измеримые показатели эффективности.

## 🔗 ИНТЕГРАЦИЯ
Связи с существующими агентами и правилами.

## 🧪 ПРИМЕРЫ ИСПОЛЬЗОВАНИЯ
Реальные сценарии применения.

## ⚠️ ОСОБЫЕ СЛУЧАИ
Edge cases и failure scenarios.
```

### Шаблон агента

Универсальный шаблон доступен в [templates/agent-template.md](templates/agent-template.md).

## 🎯 Приоритизация внедрения

### 🔴 P0 (Критические) - Фаза 1 (1-2 недели)

**Критерии P0:**
- Устраняют критические пробелы в существующих pipelines
- Блокируют автоматизацию без них
- Дают немедленный эффект (5-10x ускорение)

**Агенты:**
1. **compilation-healer** (4-6 дней)
   - Лечит ошибки компиляции через систематический анализ
   - Блокирует workflow при красной компиляции - критично для plan-task-executor
   - Честное лечение: анализирует и лечит, не удаляет код без причины

2. **systematic-plan-reviewer** (3-5 дней)
   - Автоматизирует systematic review через PowerShell скрипты
   - Заменяет 30-60 минут ручной работы на 1-2 минуты

3. **plan-readiness-validator** (5-7 дней)
   - Оценивает LLM готовность планов (≥90% score)
   - Предотвращает провальные попытки исполнения

4. **review-consolidator** (4-6 дней)
   - Координирует параллельный запуск армии ревьюеров
   - Консолидирует результаты в единый отчёт

### 🟡 P1 (Важные) - Фаза 2 (2-3 недели)

**Критерии P1:**
- Улучшают существующие workflow
- Добавляют важные функции безопасности
- Повышают качество на 30-50%

**Агенты:**
5. **git-workflow-manager** (4-6 дней)
   - Безопасный git commit/push/PR workflow
   - Предотвращает случайные коммиты и force push

6. **dependency-analyzer** (5-7 дней)
   - Анализ зависимостей при рефакторинге
   - Обнаружение vulnerabilities

### 🟢 P2 (Аналитические) - Фаза 3 (3-4 недели)

**Критерии P2:**
- Аналитика и оптимизация
- Долгосрочная поддержка качества
- Профилактика технического долга

**Агенты:**
7. **performance-profiler** (6-8 дней)
   - Профилирование производительности
   - Регрессионные тесты при рефакторинге

8. **documentation-synchronizer** (5-7 дней)
   - Синхронизация код↔документация
   - Обнаружение устаревших секций

## 🚀 Интеграция с Claude Code

### Активация агентов

**1. Явный вызов (через Task tool):**

```typescript
Task({
  subagent_type: "systematic-plan-reviewer",
  description: "Validate plan structure",
  prompt: "Analyze plan structure in docs/PLAN/feature-auth.md for violations..."
})
```

**2. Рекомендация от другого агента:**

После завершения work-plan-architect:
```
✅ work-plan-architect completed

🔄 Recommended Next Actions:
1. 🚨 CRITICAL: work-plan-reviewer
   Command: Use Task tool with subagent_type: "work-plan-reviewer"
```

Claude Code интерпретирует это как сигнал к вызову следующего агента.

### Передача контекста между агентами

**Явная передача через параметры:**
```typescript
Task({
  subagent_type: "work-plan-reviewer",
  prompt: `Review plan at docs/PLAN/feature-auth.md
           Context from architect: 8 tasks, 3 new components
           Focus: Validate against common-plan-generator.mdc`
})
```

**Неявная передача через файлы:**
- Агент А создаёт файл `docs/PLAN/feature-auth.md`
- Агент А рекомендует агента Б с параметром `plan_file`
- Агент Б читает файл и продолжает работу

## 🛡️ Защита от бесконечных циклов

### Глобальные лимиты итераций

Система агентов имеет встроенную защиту от бесконечных циклов через явные лимиты итераций и механизмы эскалации:

| Цикл | Агенты | Макс итераций | Эскалация |
|------|--------|---------------|-----------|
| **Quality Cycle** | architect ↔ reviewer | 3 | Детальный отчёт нерешённых проблем |
| **Systematic Review** | systematic ↔ architect | 2 | Violations report к пользователю |
| **Test Healing** | test-healer self-loop | 2 | Диагностика + требование arch review |
| **Execution Review** | executor modes | 2 | Через pre-completion-validator |
| **Parallel Execution** | code reviewers | N/A | Только параллельный запуск |

### Механизм эскалации

При достижении лимита итераций активируется трёхступенчатая эскалация:

**1. Формирование детального отчёта:**
```markdown
⚠️ CYCLE LIMIT REACHED - ESCALATION TO USER ⚠️

Cycle: [architect ↔ reviewer]
Iterations completed: 3/3 (limit reached)
Duration: [time elapsed]

UNRESOLVED ISSUES:
- Issue 1: [description]
  Attempted fixes: [what was tried]
  Why failed: [root cause]

- Issue 2: [description]
  Attempted fixes: [what was tried]
  Why failed: [root cause]

RECOMMENDED ACTIONS:
- Manual intervention required for architectural decisions
- Consider alternative approach: [suggestions]
- Consult with [relevant expert/team]
```

**2. Передача управления пользователю:**
- ❌ **НЕ продолжать** автономную работу
- ✅ **Предоставить** actionable рекомендации
- ✅ **Указать** возможные причины блокировки
- ✅ **Предложить** альтернативные подходы

**3. Документирование блокировки:**
- Сохранить cycle history для анализа
- Зафиксировать неустранимые проблемы
- Создать issue/task для manual resolution

### Cycle Detection & Tracking

Каждый агент при вызове следующего агента передаёт метаданные для отслеживания циклов:

```json
{
  "cycle_tracking": {
    "cycle_id": "architect-reviewer-2025-10-09-1234",
    "iteration_count": 2,
    "max_iterations": 3,
    "agents_path": ["work-plan-architect", "work-plan-reviewer", "work-plan-architect"],
    "started_at": "2025-10-09T10:30:00Z",
    "issues_history": [
      {
        "iteration": 1,
        "issues_found": 5,
        "issues_fixed": 3,
        "remaining": 2
      },
      {
        "iteration": 2,
        "issues_found": 2,
        "issues_fixed": 1,
        "remaining": 1
      }
    ]
  }
}
```

### Параллельное выполнение (code reviewers)

**code-principles-reviewer** и **code-style-reviewer** используют специальный pattern:

```markdown
**⚠️ EXECUTION MODE**: PARALLEL - use single message with multiple Task calls
**❌ ANTI-PATTERN**: Sequential execution (principles → style → principles) creates cycle
```

**Правильная реализация:**
```typescript
// ✅ CORRECT: Parallel execution (no cycle)
[
  Task({ subagent_type: "code-principles-reviewer", ... }),
  Task({ subagent_type: "code-style-reviewer", ... })
]
```

**Неправильная реализация:**
```typescript
// ❌ WRONG: Sequential execution (creates cycle)
Task({ subagent_type: "code-principles-reviewer", ... })
// wait for completion, then:
Task({ subagent_type: "code-style-reviewer", ... })
```

### Визуализация прогресса цикла

В рекомендациях агенты показывают прогресс итераций:

```markdown
🔄 Cycle Tracking: Iteration 2/3 (architect → reviewer → architect)
⚠️ Approaching iteration limit - escalation at 3 iterations

Issues Progress:
- Iteration 1: 5 issues found → 3 fixed (60% resolution)
- Iteration 2: 2 issues remaining → 1 fixed (50% resolution)
- **Remaining**: 1 unresolved issue (requires user decision)
```

### Мониторинг здоровья циклов

**Success Metrics:**
- ✅ <90% задач разрешаются за 1-2 итерации
- ✅ 5-10% требуют 3 итерации
- ⚠️ <5% достигают лимита → эскалация

**Warning Triggers:**
- 🟡 Iteration 2 без прогресса (same issues remaining)
- 🟠 Iteration 3 достигнута (approaching limit)
- 🔴 Limit reached (immediate escalation)

### Ссылки на детальный анализ

Полный анализ циклов и защиты: [../docs/AGENTS_CYCLE_PROTECTION_ANALYSIS.md](../docs/AGENTS_CYCLE_PROTECTION_ANALYSIS.md)

**Содержит:**
- Граф всех возможных переходов агентов
- Выявленные потенциальные циклы (6 типов)
- Детальные рекомендации по защите
- Таблица текущего состояния защиты (60% частично защищены, 20% не защищены, 20% полностью защищены → цель: 100%)

## 📈 Ожидаемые метрики

### После внедрения P0 агентов (Фаза 1):

| Метрика | До | После P0 | Улучшение |
|---------|-----|----------|-----------|
| **Systematic review time** | 30-60 мин | 1-2 мин | **20-30x** |
| **Plan validation coverage** | 60-70% | 95%+ | **+30%** |
| **Review consolidation** | Ручная | Автоматическая | **100%** |
| **Пропуск этапов валидации** | 20-30% | <5% | **4-6x** |

### После внедрения всех агентов (Фаза 3):

| Метрика | До | После | Улучшение |
|---------|-----|-------|-----------|
| **Full review cycle time** | 1.5-2.5 часа | 10-15 мин | **10-15x** |
| **Автоматизация workflow** | 50% | 90% | **+40%** |
| **LLM Readiness score** | Не измеряется | ≥90% | **✅ Новая метрика** |
| **Documentation drift** | 20-30% устаревшего | <5% | **4-6x** |
| **Performance regressions** | Не отслеживаются | Автоматический мониторинг | **✅ Новая метрика** |

## 🔐 Безопасность и ограничения

### Git Workflow Safety

**Критические ограничения:**
- ❌ НИКОГДА не коммитить без подтверждения пользователя
- ❌ НИКОГДА не делать force push
- ❌ НИКОГДА не коммитить в main/master без PR
- ✅ Всегда проверять authorship перед amend
- ✅ Всегда запускать pre-commit hooks

**Реализация:** git-workflow-manager агент (P1).

### Execution Limits

**Timeouts:**
- Lightweight агенты (validators): 5-10 минут
- Medium агенты (reviewers): 20-30 минут
- Heavy агенты (executors): 120-480 минут

**Iteration Limits:**
- Максимум 3 итерации для циклических этапов (architect↔reviewer)
- После 3 неудач → эскалация пользователю

**Resource Limits:**
- Максимум 5 параллельных агентов (review-consolidator)
- Автоматическая очередь при превышении

## 📚 Связанные документы

**Анализ и исследование:**
- [../docs/AGENTS_ECOSYSTEM_ANALYSIS.md](../docs/AGENTS_ECOSYSTEM_ANALYSIS.md) - комплексный анализ существующей экосистемы

**Спецификации агентов:**
- [agents/](agents/) - директория со спецификациями всех агентов
- [agents/systematic-plan-reviewer.md](agents/systematic-plan-reviewer.md) - пример P0 агента

**Шаблоны:**
- [templates/agent-template.md](templates/agent-template.md) - универсальный шаблон агента

**Интеграция:**
- [../CLAUDE.md](../CLAUDE.md) - краткая справка по агентам для пользователя
- [../docs/PLANS/](../docs/PLANS/) - планы работ для исполнения

---

**Версия:** 1.0
**Дата создания:** 2025-10-08
**Авторы:** Claude Code
**Статус:** ✅ Активная архитектура
