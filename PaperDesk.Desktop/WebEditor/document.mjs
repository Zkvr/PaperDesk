import StarterKit from '@tiptap/starter-kit'
import { Details, DetailsContent, DetailsSummary } from '@tiptap/extension-details'
import { Image } from '@tiptap/extension-image'
import { TaskItem, TaskList } from '@tiptap/extension-list'
import { TableKit } from '@tiptap/extension-table'
import { MarkdownManager } from '@tiptap/markdown'
import { NodeSelection } from '@tiptap/pm/state'

const pictureScale = 380
const validId = /^[0-9a-f-]{36}$/i

function legacyTasks(body) {
  let fence
  return body.split('\n').map(line => {
    const delimiter = /^ {0,3}(`{3,}|~{3,})/.exec(line)?.[1]
    if (delimiter) {
      if (!fence) fence = delimiter
      else if (delimiter[0] === fence[0] && delimiter.length >= fence.length &&
          /^ {0,3}(?:`+|~+)\s*$/.test(line)) fence = undefined
      return line
    }
    if (fence) return line
    return line.replace(/^([ \t]*)(?:[-+*][ \t]+)?([☐☑☒]|\[[ xX]\])[ \t]*/, (_, indent, mark) =>
      indent + '- [' + (['☑', '☒', '[x]', '[X]'].includes(mark) ? 'x' : ' ') + '] ')
  }).join('\n')
}
const spoiler = Details.extend({
  markdownTokenizer: {
    name: 'details',
    level: 'block',
    start: source => source.search(/^[ \t]*:::spoiler(?:[ \t]|$)/m),
    tokenize(source, _tokens, lexer) {
      const match = /^[ \t]*:::spoiler(?:[ \t]+([^\n]*))?[ \t]*\n([\s\S]*?)^[ \t]*:::[ \t]*(?:\n|$)/m.exec(source)
      if (!match || match.index !== 0) return undefined
      return { type: 'details', raw: match[0], title: match[1]?.trim() ?? '', tokens: lexer.blockTokens(match[2]) }
    },
  },
  parseMarkdown(token, helpers) {
    const body = helpers.parseChildren(token.tokens ?? [])
    return {
      type: 'details',
      content: [
        { type: 'detailsSummary', content: token.title ? helpers.parseInline([{ type: 'text', raw: token.title, text: token.title }]) : [] },
        { type: 'detailsContent', content: body.length ? body : [{ type: 'paragraph' }] },
      ],
    }
  },
  renderMarkdown(node, helpers) {
    const [title, body] = node.content ?? []
    const heading = helpers.renderChildren(title?.content ?? []).trim()
    const text = helpers.renderChildren(body?.content ?? [], '\n\n')
    return ':::spoiler' + (heading ? ' ' + heading : '') + '\n' + text + '\n:::'
  },
})

export function createDocumentCodec(nonce) {
  const marker = 'PDIMG-' + nonce + '-'
  const pictureToken = new RegExp('^' + marker + '([0-9a-f-]{36})[ \\t]*(?:\\n|$)', 'i')
  const layouts = new Map()
  const image = Image.extend({
    markdownTokenName: 'paperPicture',
    markdownTokenizer: {
      name: 'paperPicture',
      level: 'block',
      start: source => source.indexOf(marker),
      tokenize(source) {
        const match = pictureToken.exec(source)
        if (!match || !layouts.has(match[1])) return undefined
        return { type: 'paperPicture', raw: match[0], id: match[1] }
      },
    },
    addAttributes() {
      return {
        ...this.parent?.(),
        id: { default: null, renderHTML: attrs => ({ 'data-picture': attrs.id }) },
        mode: { default: 'inline', renderHTML: attrs => ({ 'data-mode': attrs.mode }) },
        x: { default: .18, renderHTML: attrs => ({ 'data-side': attrs.x >= .5 ? 'right' : 'left' }) },
      }
    },
    parseHTML() {
      return [{ tag: 'img[data-picture]', getAttrs: element => layouts.has(element.dataset.picture) ? null : false }]
    },
    parseMarkdown: token => pictureNode(layouts.get(token.id)),
    renderMarkdown: node => marker + node.attrs.id,
    addKeyboardShortcuts() {
      return {
        Enter: ({ editor }) => {
          const selection = editor.state.selection
          if (!(selection instanceof NodeSelection) || selection.node.type !== this.type) return false
          return editor.chain().insertContentAt(selection.from, { type: 'paragraph' })
            .setTextSelection(selection.from + 1).run()
        },
      }
    },
  }).configure({ resize: { enabled: true, directions: ['bottom-right'], minWidth: 32, minHeight: 24, alwaysPreserveAspectRatio: true } })

  const extensions = [StarterKit.configure({ link: false, trailingNode: false }), TableKit, TaskList, TaskItem, spoiler, DetailsSummary, DetailsContent, image]
  const markdown = new MarkdownManager({ extensions, markedOptions: { gfm: true, breaks: true } })

  function pictureNode(layout) {
    return {
      type: 'image',
      attrs: {
        id: layout.id, src: '/picture/' + layout.id, mode: layout.mode, x: layout.x,
        width: Math.round(layout.width * pictureScale),
      },
    }
  }

  function read(source, pictures) {
    const body = legacyTasks(source.replace(/\r\n?/g, '\n'))
    layouts.clear()
    for (const picture of pictures) if (validId.test(picture.id)) layouts.set(picture.id, picture)
    if (!layouts.size) return markdown.parse(body)
    const starts = [0]
    for (let at = body.indexOf('\n'); at >= 0; at = body.indexOf('\n', at + 1)) starts.push(at + 1)
    const boundaries = [0]
    let cursor = 0
    for (const token of markdown.instance.lexer(body)) {
      if (token.type !== 'space') boundaries.push(cursor)
      cursor += token.raw.length
    }
    boundaries.push(body.length)
    const insertions = new Map()
    for (const picture of layouts.values()) {
      const target = starts[picture.anchor] ?? body.length
      const boundary = boundaries.find(position => position >= target) ?? body.length
      if (!insertions.has(boundary)) insertions.set(boundary, [])
      insertions.get(boundary).push(marker + picture.id)
    }
    cursor = 0
    let composed = ''
    for (const [position, markers] of [...insertions].sort((a, b) => a[0] - b[0])) {
      composed += body.slice(cursor, position)
      if (composed && !composed.endsWith('\n\n')) composed += composed.endsWith('\n') ? '\n' : '\n\n'
      composed += markers.join('\n\n') + (position < body.length ? '\n\n' : '')
      cursor = position
    }
    return markdown.parse(composed + body.slice(cursor))
  }

  function write(document, pictures) {
    const known = new Map(pictures.map(layout => [layout.id, layout]))
    const active = new Map()
    function find(node) {
      if (node.type === 'image' && known.has(node.attrs.id)) active.set(node.attrs.id, node.attrs)
      node.content?.forEach(find)
    }
    find(document)
    const rows = markdown.serialize(document).split('\n')
    const body = []
    const images = []
    for (let index = 0; index < rows.length; index++) {
      const row = rows[index]
      const id = row.startsWith(marker) ? row.slice(marker.length).trim() : ''
      const attrs = active.get(id)
      if (!attrs) { body.push(row); continue }
      images.push({
        ...known.get(id), anchor: body.length, mode: attrs.mode, x: attrs.x,
        width: Math.min(1, Math.max(.08, attrs.width / pictureScale)),
      })
      if (rows[index + 1] === '') index++
    }
    return { body: body.join('\n'), images }
  }

  return { extensions, read, write }
}
