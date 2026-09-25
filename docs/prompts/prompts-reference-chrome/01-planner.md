# Prompt: fences-feature-planner — Reference Panel Chrome

Спланируй реализацию `ADR-030: Reference-Style Layered Panel Chrome` для DesktopOrganizer.

Контекст:

- .NET 8, WPF;
- текущий visual language: ADR-025 Ambient Accent;
- ключевой template: `src/DesktopOrganizer.UI/Resources/Styles/Panels.xaml`;
- control: `src/DesktopOrganizer.UI/Controls/PanelChrome.cs`;
- metrics/tokens: `PanelMetrics.cs`, resource dictionaries;
- reference: `docs/Ref image.png`;
- WorkerW/Progman integration менять не хотим;
- persistence полностью заморожен по `CLAUDE.md`.

Нужно получить:

- neutral 1 DIP border;
- subtle accent stroke;
- subtle accent glow;
- subtle surface highlight/gradient;
- curved accent marker у заголовка (`Path`, не составной прямоугольник);
- state-aware emphasis на базе существующих interaction/edit states;
- High Contrast-safe fallback;
- никакого DWM Acrylic/backdrop blur;
- никаких новых persisted settings.

План должен явно разобрать:

1. какие текущие template parts/конвертеры/токены переиспользуются;
2. где нужен новый token, converter или DP, а где он НЕ нужен;
3. как избежать clipping glow из-за Window bounds/AllowsTransparency;
4. как исключить layout jitter при state changes;
5. performance risk `DropShadowEffect` и fallback без shader/effect;
6. High Contrast strategy;
7. tests/static gates;
8. manual verify matrix Win10/Win11 и DPI;
9. по-коммитный план, желательно 4–7 логических коммитов;
10. список файлов, которые persistence freeze запрещает трогать.

После плана укажи `Recommended Next Actions` для `wpf-ui-builder`, затем `tdd-test-engineer`, затем
параллельных reviewers.
