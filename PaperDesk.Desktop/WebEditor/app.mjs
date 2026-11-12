import { Editor, Extension } from '@tiptap/core'
import { EditorState, Plugin } from '@tiptap/pm/state'
import { createDocumentCodec } from './document.mjs'

const bridge = window.chrome?.webview
const codec = createDocumentCodec(crypto.randomUUID())
const menu = document.querySelector('#menu')
let scope, loading = false, allowedImages = new Set(), readOnly = true, sentBody
const send = (type, data = {}) => bridge?.postMessage({ type, revision: scope?.revision, ...data })
const label = (ru, en) => scope?.russian ? ru : en

function imageIds(doc) {
  const ids = []
  doc.descendants((node, _position, parent) => {
    if (node.type.name === 'image') ids.push(parent === doc ? node.attrs.id : null)
  })
  return ids
}

function insertionAnchor(at) {
  const { doc, selection } = editor.state
  const position = at == null ? selection.$from : doc.resolve(at)
  const content = doc.content.cut(0, position.depth ? position.before(1) : position.pos).toJSON()
  const prefix = codec.write({ type: 'doc', content }, scope.images).body
  return prefix ? prefix.split('\n').length + 1 : 0
}

const imageOwnership = Extension.create({
  name: 'paperdeskAttachments',
  addProseMirrorPlugins() {
    return [new Plugin({
      filterTransaction(transaction) {
        if (loading || !transaction.docChanged) return true
        const ids = imageIds(transaction.doc)
        return ids.length === allowedImages.size && ids.every(id => allowedImages.has(id)) &&
          new Set(ids).size === ids.length
      },
    })]
  },
})

const editor = new Editor({
  element: document.querySelector('#editor'),
  extensions: [...codec.extensions, imageOwnership],
  content: { type: 'doc', content: [{ type: 'paragraph' }] },
  editable: false,
  editorProps: {
    attributes: { 'aria-label': 'Note editor', spellcheck: 'false' },
    handlePaste(_view, event) {
      if (readOnly || !Array.from(event.clipboardData?.items ?? []).some(item => item.type.startsWith('image/'))) return false
      send('paste', { anchor: insertionAnchor() })
      return true
    },
    handleDrop(_view, event, _slice, moved) {
      if (readOnly || moved) return false
      const file = Array.from(event.dataTransfer?.files ?? []).find(item => item.type.startsWith('image/'))
      if (!file) return false
      if (file.size <= 20 * 1024 * 1024) {
        const reader = new FileReader()
        const revision = scope?.revision
        const anchor = insertionAnchor(_view.posAtCoords({ left: event.clientX, top: event.clientY })?.pos)
        reader.onload = () => {
          if (scope?.revision === revision && typeof reader.result === 'string')
            send('attach', { data: reader.result.split(',', 2)[1], anchor })
        }
        reader.readAsDataURL(file)
      }
      return true
    },
  },
  onFocus: () => send('focus', { editing: true }),
  onBlur: () => send('focus', { editing: false }),
  onUpdate({ editor: current }) {
    if (loading || !scope || readOnly) return
    const saved = codec.write(current.getJSON(), scope.images)
    const newline = scope.body.includes('\r\n') ? '\r\n' : '\n'
    const body = saved.body.replace(/\n/g, newline)
    if (body === sentBody && JSON.stringify(saved.images) === JSON.stringify(scope.images)) return
    const before = sentBody
    sentBody = body
    scope.images = saved.images
    send('change', { ...saved, body, before })
  },
})

function load(message) {
  const same = scope?.revision === message.revision
  const selection = same ? editor.state.selection.from : 0
  const alreadyCurrent = same && sentBody === message.body && JSON.stringify(scope.images) === JSON.stringify(message.images)
  scope = message
  sentBody = message.body
  readOnly = !message.editable
  document.documentElement.lang = message.russian ? 'ru' : 'en'
  document.body.style.fontSize = message.fontSize + 'px'
  document.body.style.fontFamily = message.font + ', "Segoe UI", sans-serif'
  editor.setEditable(!readOnly, false)
  if (alreadyCurrent) return
  menu.hidden = true
  loading = true
  try {
    const json = codec.read(message.body, message.images)
    if (!json.content?.length) json.content = [{ type: 'paragraph' }]
    if (json.content.at(-1)?.type === 'image') json.content.push({ type: 'paragraph' })
    allowedImages = new Set(message.images.map(picture => picture.id))
    if (same) editor.chain().setMeta('addToHistory', false).setContent(json, { emitUpdate: false }).run()
    else {
      const { schema, plugins } = editor.state
      editor.view.updateState(EditorState.create({ schema, plugins, doc: schema.nodeFromJSON(json) }))
    }
    if (selection > 0) editor.commands.setTextSelection(Math.min(selection, editor.state.doc.content.size))
    if (!same) window.scrollTo(0, 0)
  } finally { loading = false }
}

function showMenu(entries, x = 8, y = 4) {
  menu.replaceChildren()
  for (const [caption, action] of entries) {
    const button = document.createElement('button')
    button.type = 'button'
    button.textContent = caption
    button.addEventListener('mousedown', event => event.preventDefault())
    button.addEventListener('click', () => { menu.hidden = true; action() })
    menu.append(button)
  }
  menu.hidden = false
  menu.style.left = Math.max(0, Math.min(x, innerWidth - menu.offsetWidth)) + 'px'
  menu.style.top = Math.max(0, Math.min(y, innerHeight - menu.offsetHeight)) + 'px'
}

const formatActions = {
  Bold: chain => chain.toggleBold(),
  Italic: chain => chain.toggleItalic(),
  Strike: chain => chain.toggleStrike(),
  Code: chain => chain.toggleCode(),
  Heading: chain => chain.toggleHeading({ level: 2 }),
  Bullet: chain => chain.toggleBulletList(),
  Number: chain => chain.toggleOrderedList(),
  Task: chain => chain.toggleTaskList(),
  Quote: chain => chain.toggleBlockquote(),
  Spoiler: chain => chain.setDetails(),
  Table: chain => chain.insertTable({ rows: 2, cols: 2, withHeaderRow: true }),
  AddRow: chain => chain.addRowAfter(),
  AddColumn: chain => chain.addColumnAfter(),
  RemoveRow: chain => chain.deleteRow(),
  RemoveColumn: chain => chain.deleteColumn(),
  RemoveTable: chain => chain.deleteTable(),
}

function requestFormat() {
  if (readOnly) return
  menu.hidden = true
  send('formatMenu', { inTable: editor.isActive('table') })
}

document.addEventListener('contextmenu', event => {
  const picture = event.target.closest?.('img[data-picture]')
  if (!picture) return
  event.preventDefault()
  const id = picture.dataset.picture
  const layout = scope?.images.find(item => item.id === id)
  if (!layout) return
  const entries = [
    [label('Открыть изображение', 'Open image'), () => send('image', { id, action: 'open' })],
    [label('Копировать изображение', 'Copy image'), () => send('image', { id, action: 'copy' })],
  ]
  if (!readOnly) {
    for (const [mode, caption] of [['inline', label('В тексте', 'In text')], ['overlay', label('Поверх текста', 'Over text')], ['wrap', label('Обтекание', 'Wrap text')]]) {
      entries.push([(mode === layout.mode ? '✓ ' : '') + caption, () => {
        let position
        editor.state.doc.descendants((node, at) => { if (node.type.name === 'image' && node.attrs.id === id) position = at })
        if (position !== undefined)
          editor.chain().focus().setNodeSelection(position).updateAttributes('image', { mode }).run()
      }])
    }
    entries.push([label('Удалить изображение', 'Remove image'), () => send('image', { id, action: 'remove' })])
  }
  showMenu(entries, event.clientX, event.clientY)
})
document.addEventListener('pointerdown', event => { if (!menu.contains(event.target)) menu.hidden = true })
document.addEventListener('keydown', event => {
  if (event.key === 'Escape' && !menu.hidden) { menu.hidden = true; event.preventDefault() }
  if (event.ctrlKey && event.shiftKey && event.code === 'KeyF') { event.preventDefault(); requestFormat() }
  if (event.ctrlKey && !event.shiftKey && event.code === 'KeyN') { event.preventDefault(); send('newNote') }
  if (event.shiftKey && event.key === 'Tab' && !editor.isActive('table')) { event.preventDefault(); send('title') }
})

bridge?.addEventListener('message', ({ data }) => {
  if (data.type === 'load') load(data)
  else if (data.revision === scope?.revision) {
    if (data.type === 'format') requestFormat()
    if (data.type === 'formatAction' && !readOnly && Object.hasOwn(formatActions, data.action))
      formatActions[data.action](editor.chain().focus()).run()
    if (data.type === 'focus') editor.commands.focus()
    if (data.type === 'blur') { menu.hidden = true; editor.commands.blur() }
  }
})
send('ready')
