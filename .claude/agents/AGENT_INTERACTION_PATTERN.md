# 🔄 Паттерн интерактивности между суб-агентами и основным агентом

## 🎯 Назначение

Определить правильный паттерн коммуникации между суб-агентом и основным агентом (Claude Code) в случаях, когда суб-агент теряет уверенность и не может продолжить автономно.

**Проблема:** Суб-агент НЕ должен спрашивать пользователя напрямую. Вместо этого он должен:
1. Завершить работу с промежуточными результатами
2. Сформулировать ВОПРОС основному агенту
3. Основной агент решит, ответить ли самостоятельно или перенаправить пользователю

## 📊 Жизненный цикл взаимодействия

### Фаза 1: Суб-агент теряет уверенность

```
Суб-агент:
- Выполняет анализ/исследование
- Достигает точки с confidence < 90%
- НЕ может автономно принять решение
- НЕ спрашивает пользователя
```

### Фаза 2: Суб-агент формирует CLARIFICATION REQUEST

```markdown
⚠️ **INTERMEDIATE RESULTS (Confidence: 65%)**

## Анализ выполнен:
[Что суб-агент нашел и понял]

## Что осталось неясным:
[Специфичное, что нужно уточнить]

## ❓ CLARIFICATION NEEDED (для основного агента):
```
[ВОПРОС к основному агенту, НЕ к пользователю]

Примеры формулирования:
- "Should I treat version A as authoritative or version B?"
- "Is this divergence intentional or a bad merge?"
- "Which architectural approach should I assume for the solution?"
```

## Возможные варианты ответа:
1. [Вариант 1]
2. [Вариант 2]
3. [Если основной агент не знает - escalate to user]
```

### Фаза 3: Основной агент обрабатывает CLARIFICATION

Основной агент (Claude Code) имеет 3 варианта:

#### ✅ Вариант A: Основной агент знает ответ
```
Основной агент:
1. Ответить на вопрос суб-агента
2. Повторно вызвать суб-агента с исходным запросом + ответом на вопрос

Task(
  subagent_type: "[agent-name]",
  prompt: """
  [ORIGINAL REQUEST]

  [INTERMEDIATE RESULTS from previous run]

  Ответ на твой вопрос: [ANSWER]

  Продолжи анализ с этой информацией.
  """
)
```

#### ⚠️ Вариант B: Основной агент может сделать вывод
```
Основной агент:
1. Провести собственный анализ (если это быстро)
2. Ответить на вопрос суб-агента
3. Повторно вызвать суб-агента с ответом
```

#### ❌ Вариант C: Основной агент не знает ответ
```
Основной агент:
1. Признать, что основной агент также не уверен
2. Сформулировать вопрос пользователю
3. Получить ответ от пользователя
4. Повторно вызвать суб-агента с ответом от пользователя

Формулирование для пользователя:
- Опирается на ПРОМЕЖУТОЧНЫЕ РЕЗУЛЬТАТЫ суб-агента
- Предлагает конкретные варианты ответов (A/B/C, не открытый вопрос)
- Ссылается на анализ суб-агента
```

## 📝 Примеры паттернов

### Пример 1: git-blame-merge-resolver → уточнение о file lineage

**Суб-агент (git-blame-merge-resolver) формирует:**
```markdown
⚠️ **INTERMEDIATE RESULTS (Confidence: 68%)**

## Найдено:
- Файл ProjectDeployerParams в Shared.Models потерял 3 свойства
- DmitryIonas добавил их в Core версии в fe7e965a6e
- В commit aa3154be8 файл скопирован в Shared без этих свойств

## Неясно:
- Была ли потеря свойств ОШИБКОЙ в merge, или НАМЕРЕННЫМ вычищением?
- Какая версия является source of truth: Core или Shared?

## ❓ CLARIFICATION NEEDED:
Should I treat the Core version as authoritative (свойства нужно восстановить в Shared)?
Or is Shared version intentionally simplified (свойства не нужны в параметрах)?

Возможные варианты:
A) Core - source of truth (восстанавливаем свойства в Shared)
B) Shared - intentionally simplified (удаляем из Core)
C) Нужны в обоих (разные варианты)
```

**Основной агент решает:**
- Может ответить на основе git history, commit message, других файлов
- Если не уверен → спрашивает пользователя с этими вариантами
- Повторно вызывает суб-агента: `git-blame-merge-resolver` или `work-plan-architect` с ответом

### Пример 2: codebase-researcher → уточнение о architecture

**Суб-агент формирует:**
```markdown
⚠️ **INTERMEDIATE RESULTS (Confidence: 72%)**

## Найдено:
- Существует 3 версии AuthenticationService (Core, Shared, Microservices)
- Dependency graph указывает на Core как primary
- Но Microservices версия содержит новые features

## Неясно:
- Какая версия является CURRENT/ACTIVE?
- Должны ли Shared и Microservices версии быть синхронизированы?

## ❓ CLARIFICATION NEEDED:
Which AuthenticationService is currently in use?
A) Core version (legacy)
B) Microservices version (newer)
C) Shared version (for new projects)
D) All three coexist with different purposes
```

**Основной агент:**
- Может быстро ответить (например, grep для usage)
- Или спросить пользователя с этими конкретными вариантами
- Повторно вызывает агента с ответом

## 🔒 Правила паттерна

### ✅ ПРАВИЛА ВЗАИМОДЕЙСТВИЯ:

1. **Суб-агент НИКОГДА не спрашивает пользователя напрямую**
   - ❌ "Please advise which version to use"
   - ✅ Завершает и формирует CLARIFICATION_NEEDED для основного агента

2. **Суб-агент формулирует КОНКРЕТНЫЕ вопросы**
   - ❌ "What should I do about the divergence?"
   - ✅ "Should A be treated as authoritative source or B?"

3. **Суб-агент предлагает конкретные варианты**
   - ❌ Открытые вопросы
   - ✅ A/B/C варианты с кратким описанием

4. **Основной агент может ответить ДО спроса пользователя**
   - ❌ Сразу спрашивать пользователя
   - ✅ Сначала попробовать ответить самостоятельно
   - ✅ Спросить пользователя только если сам не уверен

5. **При повторном вызове передаёте оба контекста**
   - Исходный запрос
   - Промежуточные результаты
   - Ответ на CLARIFICATION

### ❌ АНТИПАТТЕРНЫ:

- Суб-агент спрашивает пользователя (`AskUserQuestion` tool)
- Суб-агент требует escalation вместо CLARIFICATION
- Основной агент не пробует ответить перед escalation
- CLARIFICATION содержит открытые вопросы вместо вариантов

## 🔄 Повторный вызов суб-агента

**Формат промпта:**
```markdown
[ORIGINAL REQUEST FROM USER]

[IMPORTANT CONTEXT]
You previously reached this intermediate result:
[COPY INTERMEDIATE RESULTS SECTION]

[CLARIFICATION PROVIDED]
To answer your clarification question about [topic]:
The answer is: [SPECIFIC ANSWER WITH REASONING]

Please continue your analysis with this information and deliver final results.
```

## 📊 Метрики успеха

- ✅ Суб-агент **никогда** не спрашивает пользователя напрямую
- ✅ Основной агент отвечает на 60-70% CLARIFICATION без escalation
- ✅ Когда escalation нужен → вопрос имеет конкретные варианты
- ✅ Повторный вызов агента с ответом → суб-агент успешно завершает работу

## 🔗 Применение к git-blame-merge-resolver

Этот паттерн особенно важен для `git-blame-merge-resolver`:

```
git-blame-merge-resolver:
  confidence >= 90%
    → Рекомендует next agent (work-plan-architect или codebase-researcher)

  confidence 70-90%
    → Пробует deeper analysis (authorship, timing, patterns)
    → Если still < 90%
       → Формирует CLARIFICATION_NEEDED для основного агента
       → Останавливается (не спрашивает пользователя)

  confidence < 70%
    → Максимально пробует deeper analysis
    → Если all analysis exhausted
       → Формирует CLARIFICATION_NEEDED с вариантами A/B/C
       → Останавливается
```

## 🧪 Примеры для документирования в агентах

Каждый агент должен добавить в раздел "🔄 АВТОМАТИЧЕСКИЕ РЕКОМЕНДАЦИИ":

```markdown
### При потере уверенности (confidence < 90%):

**ДО ESCALATION - ПОПРОБОВАТЬ:**
1. [Тактика 1 для восстановления уверенности]
2. [Тактика 2 для восстановления уверенности]
3. [Тактика 3 для восстановления уверенности]

**ЕСЛИ TACTICS НЕ ПОМОГАЮТ - CLARIFICATION_NEEDED:**
```
⚠️ **INTERMEDIATE RESULTS (Confidence: XX%)**

[Результаты анализа]

## ❓ CLARIFICATION NEEDED (для основного агента):
[Конкретный вопрос]

Варианты ответа:
A) [Вариант 1]
B) [Вариант 2]
C) [Вариант 3]
```

---

## 📚 Применение к существующим агентам

### Агенты, которые должны использовать этот паттерн:
- **git-blame-merge-resolver** (уже реализован)
- **codebase-researcher** (confidence < 90% на file origins)
- **work-plan-architect** (confidence < 90% на resolution)
- **test-healer** (confidence < 90% на root cause)
- **code-principles-reviewer** (confidence < 90% на violations)

### Агенты, которые не используют:
- Агенты с binary outcomes (план хорош или нет)
- Агенты с clear success/failure criteria
- Агенты, которые всегда могут выполнить свою роль

