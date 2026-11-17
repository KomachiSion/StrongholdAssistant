import test from 'node:test'
import assert from 'node:assert/strict'
import { encodeLineup, decodeLineup } from '../src/lib/share.ts'

const catalog = { covenants: [{ id: 'c' }], operators: Array.from({length: 12}, (_, i) => ({ name: `op${i}` })), equipment: [{name:'eq'}], strategies: [] }
const base = { id:'test', name:'推荐测试', goals:[], members:[{name:'op0',key:true},{name:'op1'}], equipment:[], strategyId:null, source:'user', updatedAt:'' }
const roundtrip = (lineup) => {
  const code = encodeLineup(lineup, catalog)
  const result = decodeLineup(code, catalog)
  assert.equal(result.ok, true)
  return { code, lineup: result.lineup }
}
test('legacy SA1–SA4 continue to encode and decode unchanged without recommended members', () => {
  for (const [version, extra] of [[1, {}], [2,{members:[{name:'op0',equipment:[{name:'eq',priority:true}]}]}], [3,{stages:[{name:'早期',fromRound:1,toRound:2,goals:[],members:[],equipment:[]}]}], [4,{groups:[{need:1,members:[{name:'op1'}]}]}]]) {
    const {code} = roundtrip({...base,...extra})
    assert.ok(code.startsWith(`SA${version}-`))
  }
})
test('recommended priority survives sharing in final, group, stage and stage group members', () => {
  const member = {name:'op1',recommended:true}
  const group = {need:1,members:[member,{name:'op2',key:true}]}
  const {code,lineup} = roundtrip({...base,members:[...base.members.map(m=>({...m})), {name:'op3',recommended:true}],groups:[group],stages:[{name:'早期',fromRound:1,toRound:3,goals:[],members:[member],equipment:[],groups:[group]}]})
  assert.ok(code.startsWith('SA5-'))
  assert.equal(lineup.members[2].recommended,true)
  assert.equal(lineup.groups[0].members[0].recommended,true)
  assert.equal(lineup.stages[0].members[0].recommended,true)
  assert.equal(lineup.stages[0].groups[0].members[0].recommended,true)
  assert.equal(lineup.groups[0].need,1)
})
test('key wins conflicting flags and recommendation bitmap crosses byte boundaries', () => {
  const members = catalog.operators.map((m,i)=>({...m, recommended:true, ...(i===0?{key:true}:{})}))
  const {lineup} = roundtrip({...base,members})
  assert.equal(lineup.members[0].key,true)
  assert.equal(lineup.members[0].recommended,undefined)
  assert.equal(lineup.members[8].recommended,true)
  assert.equal(lineup.members[11].recommended,true)
})
test('truncated SA5 is rejected instead of silently dropping recommendations', () => {
  const {code}=roundtrip({...base,members:[{name:'op0',recommended:true}]})
  assert.equal(decodeLineup(code.slice(0,-4),catalog).ok,false)
})
