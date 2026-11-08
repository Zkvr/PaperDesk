import test from 'node:test'
import assert from 'node:assert/strict'
import { getSchema } from '@tiptap/core'
import { EditorState, TextSelection } from '@tiptap/pm/state'
import { splitBlock, joinBackward } from '@tiptap/pm/commands'
import { createDocumentCodec } from './document.mjs'

const id = '69d6d866-d8c3-4c45-a86e-7ed9690357d1'
const codec = createDocumentCodec('test-session')
const schema = getSchema(codec.extensions)
const picture = { id, anchor: 1, mode: 'inline', x: .18, y: .14, width: .6 }

test('legacy table, task and spoiler remain editable around an existing attachment', () => {
  const body = '| Имя | План |\n| --- | --- |\n| А | Б |\n\n- [x] Готово\n\n:::spoiler Текст\nВнутри\n:::\n\nПосле'
  const document = codec.read(body, [picture])
  const model = schema.nodeFromJSON(document)
  model.check()
  assert.deepEqual(document.content.map(node => node.type), ['table', 'image', 'taskList', 'details', 'paragraph'])
  const saved = codec.write(document, [picture])
  assert.doesNotMatch(saved.body, /test-session|pdimg/)
  assert.match(saved.body, /:::spoiler Текст\nВнутри\n:::/)
  assert.deepEqual(codec.read(saved.body, saved.images), document)
})

test('ProseMirror Enter and Backspace insert and remove a paragraph before an image', () => {
  const doc = schema.nodeFromJSON(codec.read('До\n\nПосле', [{ ...picture, anchor: 2 }]))
  const endOfFirst = doc.firstChild.nodeSize - 1
  let state = EditorState.create({ doc, selection: TextSelection.create(doc, endOfFirst) })
  assert.equal(splitBlock(state, tr => { state = state.apply(tr) }), true)
  const inserted = codec.write(state.doc.toJSON(), [picture])
  assert.ok(inserted.images[0].anchor > 2, 'Enter must move the image down')
  assert.equal(joinBackward(state, tr => { state = state.apply(tr) }), true)
  const removed = codec.write(state.doc.toJSON(), [picture])
  assert.equal(removed.images[0].anchor, 2, 'Backspace must remove the empty space')
})

test('older checkbox markers and empty tasks open as editable task items', () => {
  const source = '☐ First\n☑ Done\n- [ ]\n\n```\n☑ literal\n```'
  const document = codec.read(source, [])
  schema.nodeFromJSON(document).check()
  const [tasks, code] = document.content
  assert.deepEqual(tasks.content.map(item => item.attrs.checked), [false, true, false])
  assert.equal(code.content[0].text, '☑ literal')
  const saved = codec.write(document, [])
  assert.deepEqual(codec.read(saved.body, []), document)
})
