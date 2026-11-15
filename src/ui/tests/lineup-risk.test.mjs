import test from 'node:test'
import assert from 'node:assert/strict'
import { evaluateLineup, compareLineupRisk } from '../src/lib/lineup-risk.ts'
const match = { source: 'recognized', awaitingDetail: false, bansScanned: true, bannedOperators: ['A', 'B'], covenants: [{ id: 'goal', incomplete: false }], banGroups: [{ covenant: 'goal', complete: true, slots: [{ name: 'A' }, { name: 'B' }] }] }
const lineup = (members, goals = []) => ({ members, goals })
const assess = (members, override = {}) => evaluateLineup(lineup(members), { ...match, ...override })
test('one key ban is worse than two ordinary bans', () => {
  const key = assess([{ name: 'A', key: true }])
  const other = assess([{ name: 'A' }, { name: 'B' }, { name: 'C', key: true }])
  assert.ok(compareLineupRisk(key, other) > 0)
  assert.deepEqual(key.keyBans, ['A'])
  assert.deepEqual(other.otherBans, ['A', 'B'])
})
test('key loss outranks goal limitation, then ordinary loss; same risk uses counts', () => {
  const key = assess([{ name: 'A', key: true }])
  const goal = evaluateLineup(lineup([{ name: 'C', key: true }], [{ id: 'goal', level: 1 }]), { ...match, covenants: [{ id: 'goal', incomplete: true }] })
  assert.ok(compareLineupRisk(key, goal) > 0)
  assert.ok(compareLineupRisk(goal, assess([{ name: 'A' }])) > 0)
  assert.ok(compareLineupRisk(assess([{ name: 'A', key: true }, { name: 'B', key: true }]), key) > 0)
  assert.equal(compareLineupRisk(key, key), 0)
})
test('legacy unmarked lineup is not represented as core intact', () => {
  const result = assess([{ name: 'C' }])
  assert.equal(result.hasKeys, false)
  assert.equal(result.label, '暂未发现禁用冲突')
})
test('unknown, partial, inferred, unscanned, empty, or missing match stays uncertain', () => {
  for (const override of [{ source: 'none' }, { awaitingDetail: true }, { bansScanned: false }, { banGroups: [] }, { banGroups: [{ complete: false, slots: [] }] }, { banGroups: [{ complete: true, slots: [{ name: null }] }] }, { banGroups: [{ complete: true, slots: [{ name: 'A', inferred: true }] }] }]) {
    assert.equal(assess([{ name: 'C', key: true }], override).uncertain, true)
  }
  assert.equal(assess([{ name: 'C', key: true }]).uncertain, false)
})
test('known key ban remains a warning even when input is incomplete', () => {
  const result = assess([{ name: 'A', key: true }], { bansScanned: false })
  assert.equal(result.label, '关键干员受限')
  assert.equal(result.uncertain, true)
})
test('final lineup assessment does not include obsolete early-stage members or mutate input', () => {
  const input = { ...lineup([{ name: 'C', key: true }]), stages: [{ members: [{ name: 'A', key: true }] }] }
  const before = JSON.stringify(input)
  assert.deepEqual(evaluateLineup(input, match).keyBans, [])
  assert.equal(JSON.stringify(input), before)
})

test('same risk prefers explicitly configured key operators over legacy unmarked lineups', () => {
  assert.ok(compareLineupRisk(assess([{name: 'C', key: true}]), assess([{name: 'C'}])) < 0)
})
