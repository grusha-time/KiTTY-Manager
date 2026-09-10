# Сборка, тесты и Windows ZIP

Раздел для разработчиков. Пользователю готового дистрибутива достаточно [инструкции установки](../install-instruction.md).

## Зависимости

- .NET SDK 8 для сборки.
- Bash, Python 3 и `jar` из JDK для штатной Linux-упаковки.
- `downloads/kitty_portable-0.76.1.13.exe` для включения portable KiTTY (скачивается отдельно).
- `vendor/ansible-runtime` с QEMU/Linux/лицензиями/manifest (тяжёлый двоичный пакет, не хранится в git; готовится или скачивается отдельно).

Проект WPF собирается для Windows; кросс-сборка на Linux разрешена `EnableWindowsTargeting`. Она не позволяет запустить WPF-окно на Linux. Готовое приложение — `win-x64`, self-contained, single-file.

## Подготовка SDK и проверки

Из корня репозитория:

```sh
scripts/project-source.sh install
scripts/project-source.sh check
```

`install` использует доступный SDK или устанавливает локальный `.dotnet`, затем восстанавливает NuGet-зависимости. `check` делает Release-сборку и запускает SelfTest. Для одного тестового раздела:

```sh
dotnet run --project src/KiTTYManager.SelfTest -- --filter HelpSearch
```

Если системного `dotnet` нет, используйте `.dotnet/dotnet`. Пакет SDK из некоторых Linux-дистрибутивов может не содержать WindowsDesktop targets (ошибка MSB4019); для WPF используйте полноценный локальный SDK в `.dotnet`, как штатный упаковщик. Упаковщик сейчас обращается именно к `.dotnet/dotnet`: перед упаковкой этот путь должен существовать. [Мутации и ограничения тестов](Test-Audit.md).

Для установки полноценного SDK в ожидаемую упаковщиком папку (требуется сеть):

```sh
mkdir -p .tools
curl -fsSL https://dot.net/v1/dotnet-install.sh -o .tools/dotnet-install.sh
bash .tools/dotnet-install.sh --channel 8.0 --install-dir .dotnet
scripts/project-source.sh install
```

## Ansible runtime
 
Он не возникает от обычного `dotnet build` и не хранится в git из-за размера (образ Linux ~384 МБ). Подробности в [packaging/ansible-runtime/README.md](../packaging/ansible-runtime/README.md).
 
```sh
# Проверить уже подготовленный комплект:
python3 packaging/verify-ansible-runtime.py vendor/ansible-runtime
# Подготовить новый runtime средствами Linux (отдельная тяжёлая операция):
bash packaging/build-ansible-runtime.sh
# Либо импортировать подготовленную папку; существующую цель скрипт не перезаписывает:
bash packaging/prepare-ansible-runtime.sh /path/to/prepared-runtime
```

Создание runtime скачивает компоненты и требует подходящего Linux-окружения/прав. Для сборки дистрибутива можно либо запустить сборку runtime через `packaging/build-ansible-runtime.sh`, либо импортировать готовый `vendor/ansible-runtime` (например, распаковать из `Runtime/Ansible` официального релиза KiTTY Manager).

## Выпустить ZIP локально

```sh
bash packaging/build-package.sh
```

Скрипт проверяет runtime до и после копирования, публикует приложение, собирает:

```text
dist/KiTTYManager-2.0.0-windows-x64.zip
  KiTTYManager.exe
  KiTTY/kitty.exe, kitty.ini, LICENCE.TXT
  Runtime/Ansible/...
```

Публичные Markdown-инструкции, `wiki/` и `examples/` остаются в репозитории и не копируются в релизный ZIP. Для их автономного использования скачайте исходники репозитория отдельно; встроенная справка приложения не зависит от Markdown-файлов.

Перед упаковкой **очищаются `dist` и `build/package`**. Не храните там единственную копию важных данных. В ZIP не должно быть `Data`, личных сессий/ключей, локальных планов, истории чатов или логов тестового стенда. SelfTest exe не входит в пользовательский ZIP.

Проверка результата:

```sh
python3 -m zipfile -t dist/KiTTYManager-2.0.0-windows-x64.zip
sha256sum dist/KiTTYManager-2.0.0-windows-x64.zip
```

Дополнительно проверьте содержимое архива и работу на Windows: открытие справки/поиск, сохранение карточки, SSH, используемые Firefox/WinSCP/Ansible-сценарии. Локальный commit не публикует ни GitHub release, ни ZIP; публикация выполняется отдельным действием.

## Архив исходников

```sh
scripts/project-source.sh pack backups/source-before-change.tar.gz
```

Скрипт упаковывает исходники, публичные инструкции и примеры без build-артефактов, SDK, runtime-пользовательских данных и приватных планов. Готовый vendor Ansible runtime не является частью компактного архива исходников: его нужно готовить отдельно.
