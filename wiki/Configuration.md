# Конфигурация

## Файлы

| Файл | Назначение |
|------|-----------|
| `Data\config.json` | Основная конфигурация (зашифрована DPAPI) |
| `Data\jumphost-consoles.json` | Учёт запущенных KiTTY-консолей |
| `KiTTY\Sessions\*` | Исходные KiTTY-сессии (не изменяются менеджером) |

## Шифрование

- Секреты (пароли, TOTP, passphrase) шифруются **DPAPI**
- Привязка к учётной записи Windows на конкретном компьютере
- `config.json` нельзя открыть на другом компьютере без дешифрования
- При первом запуске новой версии старый `config.json` с открытыми секретами автоматически перезаписывается в зашифрованном виде

## Основные поля конфигурации

```json
{
  "SchemaVersion": 3,
  "Groups": [],
  "UngroupedServers": [],
  "Links": [],
  "BaseProxies": [],
  "KittyPath": "KiTTY\\kitty.exe",
  "FirefoxPath": "firefox.exe",
  "FirefoxProfile": "kitty-manager",
  "ConnectionTimeoutSeconds": 60
}
```

## Модель сессии

| Поле | Описание |
|------|----------|
| `Name` | Название сессии |
| `Host` | IP или DNS-имя |
| `Port` | SSH-порт |
| `Username` | Логин |
| `Password` | Пароль (зашифрован) |
| `PrivateKeyPath` | Путь к PPK/PEM-ключу |
| `PrivateKeyPassphrase` | Passphrase ключа (зашифрован) |
| `EscalationCommand` | Команда повышения прав (`su -`, `sudo -i`) |
| `RootPassword` | Root-пароль (зашифрован) |
| `ShellPrompt` | Устойчивая часть приглашения |
| `KittyCommand` | Команда после входа в KiTTY |
| `RequiredPreviousServerId` | «Маршрут через» — обязательный последний переход |
| `TryDirectWithoutJumphost` | Сначала пробовать напрямую |
| `BackupEndpoints` | Резервные адреса (ip:port) |
| `WebInterfaces` | Список веб-интерфейсов |
| `PreferredRoute` | Запомненный маршрут |
| `PreferredProxyId` | Предпочтительная JH |
| `EndpointPreferences` | Запомненные адреса по контекстам |

## Модель точки входа

| Поле | Описание |
|------|----------|
| `Name` | Название |
| `Host` | Адрес SOCKS5 (обычно `127.0.0.1`) |
| `Port` | Порт SOCKS5 (0 = авто) |
| `SessionId` | Привязанная сессия KiTTY |
| `AutoStart` | Автостарт при запуске менеджера |
| `TotpSecret` | TOTP secret (зашифрован) |
| `TotpAlgorithm` | SHA1 / SHA256 / SHA512 |
| `TotpDigits` | 6–8 |
| `TotpPeriod` | Период в секундах |
| `AccessCommand` | Команда после входа |
| `AccessPrompt` | Ожидаемый prompt |
| `RepeatPassword` | Повтор пароля после скрипта |
| `RestartIntervalMinutes` | Интервал планового перезапуска |

## Модель связи

| Поле | Описание |
|------|----------|
| `SourceServerId` | Исходный сервер |
| `TargetServerId` | Целевой сервер |
| `IsVerified` | Подтверждена ли связь |
| `LatencyMs` | Измеренная задержка |
| `UsedProxyId` | Использованная JH |
| `LastChecked` | Время последней проверки |

## Журнал

- По умолчанию **отключён**
- Включается в «Настройках»
- Секреты в журнал не пишутся
- Может содержать внутренние имена серверов и адреса — не публикуйте без проверки
