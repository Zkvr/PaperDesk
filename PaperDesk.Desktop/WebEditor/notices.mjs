import { readFile, readdir, writeFile } from 'node:fs/promises'

const { packages } = JSON.parse(await readFile('package-lock.json', 'utf8'))
const sections = ['PaperDesk Windows editor — third-party notices']
for (const [directory, info] of Object.entries(packages)) {
  if (!directory.startsWith('node_modules/') || info.dev) continue
  const metadata = JSON.parse(await readFile(directory + '/package.json', 'utf8'))
  sections.push(metadata.name + ' ' + info.version + ' — ' + metadata.license)
  for (const file of await readdir(directory)) {
    if (/^(license|licence|notice)(\.|$)/i.test(file)) sections.push(await readFile(directory + '/' + file, 'utf8'))
  }
}
await writeFile('THIRD-PARTY-NOTICES.txt', sections.join('\n\n'), 'utf8')
