# Сборка

## Требования

| Компонент | Версия | Примечание |
|-----------|--------|-----------|
| .NET SDK | 8.0 | Или локальная установка через скрипт |
| JDK | Любая с `jar` | Для создания ZIP |
| kitty_portable | 0.76.1.13 | Поместить в `downloads/` |

## Локальная установка .NET SDK

```bash
./scripts/project-source.sh install
```

Устанавливает SDK в `.dotnet/` и NuGet-пакеты в `.nuget/` внутри проекта. Системный .NET не затрагивается.

## Сборка и тесты

```bash
./scripts/project-source.sh check
```

Выполняет:
1. `dotnet build -c Release` для KiTTYManager.App
2. Запуск KiTTYManager.SelfTest (offline-тесты)

## Сборка дистрибутива

```bash
./packaging/build-package.sh
```

Создаёт `dist/kitty-manager-windows-x64.zip` со структурой:

```
KiTTYManager.exe          # Self-contained, .NET 8 не нужен
KiTTY/
├── kitty.exe             # KiTTY 0.76.1.13
├── kitty.ini             # Конфигурация KiTTY
└── LICENCE.TXT           # Лицензия KiTTY
example-config.json       # Пример конфигурации
```

### Параметры публикации

```
-r win-x64
--self-contained true
-p:PublishSingleFile=true
-p:IncludeNativeLibrariesForSelfExtract=true
```

## Архив исходников

```bash
./scripts/project-source.sh pack [путь-к-архиву]
```

Создаёт tar.gz без build-артефактов, SDK и зависимостей. По умолчанию сохраняет в `backups/`.

## Структура для разработки

```
src/
├── KiTTYManager.App/       # WPF GUI (net8.0-windows)
├── KiTTYManager.Core/      # Логика (net8.0)
└── KiTTYManager.SelfTest/  # Тесты (net8.0)
packaging/
├── build-package.sh        # Сборка дистрибутива
├── run-tests.cmd           # Запуск тестов на Windows
├── kitty.ini               # Конфигурация KiTTY
└── sample-config.json      # Пример конфигурации
scripts/
└── project-source.sh       # install / check / pack
vendor/
└── KITTY-LICENCE.TXT       # Лицензия KiTTY
```

## Известный баг KiTTY

KiTTY 0.76.1.13 падает на AMD CPU при открытии Change Settings во время активной сессии (`sshprng.c`).

Фикс уже включён в `packaging/kitty.ini`:

```ini
[Debug]
randomactive=no
```
