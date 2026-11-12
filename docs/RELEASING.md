# Сборка и выпуск PaperDesk для Windows

## Версия и пакеты

Версию задаёт `Version` в `PaperDesk.Desktop/PaperDesk.Desktop.csproj`. Используем `MAJOR.MINOR.PATCH`. Изменения, ломающие совместимость, повышают `MAJOR`. Совместимые возможности повышают `MINOR`, исправления повышают `PATCH`. Суффиксы `-preview.N`, `-beta` и `-rc` обозначают предварительные версии. Тег совпадает с версией и получает префикс `v`. Изменения записываем в [CHANGELOG.md](../CHANGELOG.md).

Для воспроизводимой сборки используйте SDK из `global.json` и Inno Setup 7.1.1 x64. CI проверяет хэши дистрибутива компилятора и его `ISCC.exe`. Сборка фиксирует порядок и время записей ZIP. Inno Setup исключает исходное время файлов из установщика. Сравнение SHA-256 выполняйте для неподписанных сборок, полученных из одинаковых исходников с закреплёнными инструментами. [Inno Setup и проверка загрузок](https://jrsoftware.org/isdl-verify.php).

```powershell
./scripts/package-release.ps1 -Compiler '<путь к ISCC.exe>' -Output artifacts/rc -ExpectedTag v0.7.0-rc
$manifest = ./scripts/write-winget-manifests.ps1 -Release artifacts/rc
winget validate --manifest $manifest
```

Workflow `Windows` собирает ZIP для веток и pull request. Для тегов и ручного запуска он собирает также установщик и манифесты WinGet. При сборке тега проверяется его совпадение с версией. Результаты доступны как артефакт Actions. Для релиза скачайте установщик, ZIP и `SHA256SUMS.txt` из одного прогона и сверьте хэши. `release.json` хранит номер, тег и признак prerelease.

## GitHub Releases и WinGet

Черновик собирается по уже существующему тегу. Перед публикацией откройте его, проверьте загруженные файлы и пометку **Pre-release**. Для предварительных версий `Latest` выключен. Для стабильной версии используйте тег без суффикса и обычную маркировку. [Документация GitHub CLI](https://cli.github.com/manual/gh_release_create).

```powershell
$build = 'artifacts/rc'
$release = Get-Content "$build/release.json" -Raw | ConvertFrom-Json
$flags = @('--verify-tag', '--draft', '--repo', 'Zkvr/PaperDesk', '--title', "PaperDesk $($release.version)", '--notes-file', "$build/release-notes.md")
if ($release.prerelease) { $flags += @('--prerelease', '--latest=false') }
gh release create $release.tag @flags "$build/PaperDesk-Setup-win-x64.exe" "$build/PaperDesk-Portable-win-x64.zip" "$build/SHA256SUMS.txt"
```

Предварительный манифест использует идентификатор `Zkvr.PaperDesk.Preview`. Манифест фиксирует URL файла конкретного релиза, SHA-256 и идентификатор деинсталлятора Inno Setup. Для подачи в каталог сначала должен быть доступен конечный файл по указанному URL. `winget validate` проверяет структуру локально. Проверки загрузки и установки выполняются при подаче в каталог. [Порядок проверки WinGet](https://learn.microsoft.com/en-us/windows/package-manager/package/winget-validation).

## Подпись и канал обновлений

Основной канал — установщик и ZIP из версионированных GitHub Releases. В RC обновление ручное. Для публичных подписанных обновлений используем проверяемый Authenticode одного издателя и SHA-256 итоговых пакетов. WinGet может получать тот же установщик с его закреплённым хэшем. SHA-256 сверяет содержимое файла с опубликованным артефактом. Authenticode подтверждает издателя.

Для подписи требуется подходящая учётная запись издателя. Azure Artifact Signing доступен для определённых стран и типов издателей. Альтернатива — сертификат подписи кода от доверенного центра. Перед выпуском подписываем EXE приложения и используем `SignTool` в Inno Setup для установщика, деинсталлятора и временных копий Setup. Проверяем цепочку, SHA-256 и доверенную метку времени. Затем заново считаем хэши итоговых пакетов и генерируем WinGet-манифесты. Текущий локальный RC собран без Authenticode. [Варианты подписи Windows](https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options), [SignTool в Inno Setup](https://jrsoftware.org/ishelp/topic_setup_signtool.htm).

Microsoft Store может подписывать MSIX после сертификации. Для публикации текущего EXE через Store потребуются подпись издателя для установщика и PE-файлов, постоянный версионированный HTTPS URL и тихая офлайн-установка. Переход на MSIX потребует отдельной проверки переноса профиля и автозапуска. Для RC используется существующий путь через GitHub. [Требования Store к MSI/EXE](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-package-requirements), [требования к MSIX](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/package-version-numbering).

## Лицензия и сторонние компоненты

Для открытого исходного кода предлагается MIT, если владелец готов разрешить коммерческое использование, изменения и распространение исходников и сборок с сохранением уведомления. Apache-2.0 добавляет явные патентные условия. Если важно ограничить права на распространение продукта другими издателями, условия нужно выбирать отдельно. У RC пока нет утверждённой лицензии на исходный код PaperDesk. [MIT](https://opensource.org/license/mit), [Apache-2.0](https://www.apache.org/licenses/LICENSE-2.0).

Уведомления зависимостей лежат в `licenses/` и `WebEditor/THIRD-PARTY-NOTICES.txt`. При изменении зависимостей сверьте их с фактическим содержимым ZIP и установщика. Лицензия Inno Setup 7.1.1 распространяется отдельно от лицензий приложения. [Лицензия Inno Setup](https://github.com/jrsoftware/issrc/blob/is-7_1_1/license.txt).
