# Архитектура

## Структура проекта

```
src/
├── KiTTYManager.App/       # WPF GUI-слой
├── KiTTYManager.Core/      # Бизнес-логика (без UI-зависимостей)
└── KiTTYManager.SelfTest/  # Offline-тесты + диагностика
```

## KiTTYManager.App

GUI-слой на WPF. Отвечает за:

- Главное окно с деревом групп и списком сессий
- Диалоги настроек (точки входа, сессии, веб-интерфейсы)
- Карту связей (canvas с pan/zoom)
- Трей-иконку и поведение крестика
- Поиск по сессиям и группам

**Ключевые файлы:**

| Файл | Назначение |
|------|-----------|
| `MainWindow.xaml.cs` | Главное окно, дерево, список, контекстные меню |
| `LinkMapWindow.xaml.cs` | Интерактивная карта связей |
| `ProxySettingsDialog.xaml.cs` | Настройка точек входа |
| `SecretBox.xaml.cs` | Поле с кнопкой-глазом для секретов |
| `DarkWindowChrome.cs` | Тёмная тема окон без стандартного chrome |

## KiTTYManager.Core

Ядро логики. Не зависит от WPF. Отвечает за:

### Маршрутизация
| Файл | Назначение |
|------|-----------|
| `RoutePlanner.cs` | Построение списка кандидатов-маршрутов |
| `RoutePreferencePolicy.cs` | Ранжирование и выбор лучшего маршрута |
| `RouteFailureCache.cs` | Кэш неудач (90 сек для пар, 30 сек для endpoint) |
| `ServerEndpointPolicy.cs` | Выбор адреса (основной/резервный) по контексту |
| `EndpointFailureCache.cs` | Кэш неудачных endpoint |

### Подключение
| Файл | Назначение |
|------|-----------|
| `SshConnectionService.cs` | SSH-подключения, туннели, multi-hop |
| `Socks5ConsoleBridge.cs` | SOCKS5-мост для KiTTY-консоли |
| `Socks5TcpProbe.cs` | TCP-зонд через SOCKS5 |
| `DirectConsoleBridge.cs` | Прямое подключение без JH |

### KiTTY-интеграция
| Файл | Назначение |
|------|-----------|
| `KittySessionImporter.cs` | Импорт сессий из `KiTTY\Sessions` |
| `KittySessionWriter.cs` | Запись изменений в KiTTY-файлы |
| `KittyLaunchPlan.cs` | Формирование аргументов запуска kitty.exe |
| `KittyRoutedSession.cs` | Создание временной копии сессии для маршрута |
| `KittyCredentialDecoder.cs` | Расшифровка паролей KiTTY (scramble + MASKPASS) |
| `KittyLoginScript.cs` | Обработка login script |

### Jumphost
| Файл | Назначение |
|------|-----------|
| `JumphostStartupPlan.cs` | План запуска JH (OTP, скрипт, повтор пароля) |
| `JumphostPortSelector.cs` | Выбор свободного порта для SOCKS5 |
| `JumphostConsoleStore.cs` | Учёт запущенных консолей |
| `JumphostConsoleTitles.cs` | Заголовки окон для идентификации |
| `JumphostConsoleCleanupPolicy.cs` | Поиск и закрытие дублей |
| `AccessGrantPolicy.cs` | Политика отправки access-скрипта |
| `AccessScriptConsolePolicy.cs` | Выбор консоли для скрипта |

### Веб-интерфейсы
| Файл | Назначение |
|------|-----------|
| `FirefoxProfileWorkspace.cs` | Создание временных Firefox-профилей |
| `ResolvingHttpProxy.cs` | HTTP/HTTPS-прокси с резолвингом доменов |
| `ResolvingSocks5Relay.cs` | SOCKS5-relay с резолвингом |
| `WebResolverMappingPlan.cs` | План маппинга домен → IP |

### Конфигурация
| Файл | Назначение |
|------|-----------|
| `Models.cs` | Все модели данных |
| `ConfigStore.cs` | Чтение/запись `config.json` |
| `ConfigSecretProtection.cs` | Шифрование/дешифрование DPAPI |
| `ConfigTransfer.cs` | Экспорт/импорт JSON |
| `ConfigIndex.cs` | Индексы для быстрого поиска |

### Прочее
| Файл | Назначение |
|------|-----------|
| `TotpGenerator.cs` | Генерация TOTP-кодов (SHA1/SHA256/SHA512) |
| `PrivateKeyInspector.cs` | Определение типа SSH-ключа |
| `LinkMapLayout.cs` | Раскладка графа для карты связей |
| `BackgroundProbeRegistry.cs` | Фоновые допроверки маршрутов |
| `RuntimeLogWriter.cs` | Журнал работы (опциональный) |

## KiTTYManager.SelfTest

Консольное приложение с offline-тестами:

- Тесты маршрутизации (без реальных SSH)
- Тесты импорта/экспорта
- Тесты SOCKS5-relay и HTTP-прокси (на localhost)
- Тесты TOTP-генератора
- Диагностика комплекта пакета

Запуск: `KiTTYManager.SelfTest.exe --diagnostics --root <path>`

## Зависимости

| Пакет | Версия | Назначение |
|-------|--------|-----------|
| SSH.NET | 2025.1.0 | SSH-клиент, туннели |
| WPF | .NET 8.0 | GUI |

Внешние бинарники (не NuGet):
- `kitty.exe` — KiTTY 0.76.1.13
- `firefox.exe` — системный Firefox
