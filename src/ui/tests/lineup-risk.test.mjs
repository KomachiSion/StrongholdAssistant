import test from 'node:test'
import assert from 'node:assert/strict'
import { evaluateLineup as risk, compareLineupRisk as compare } from '../src/lib/lineup-risk.ts'
const match = {bannedOperators:['key','rec','normal','alt'],covenants:[],source:'ocr',bansScanned:true,banGroups:[{complete:true,slots:[{name:'key'}]}]}
const lineup = (members=[],groups=[]) => ({members,goals:[],groups})
test('priority ranks key then recommended then ordinary, regardless of lower-priority counts',()=>{
 const key=risk(lineup([{name:'key',key:true}]),match)
 const rec=risk(lineup([{name:'rec',recommended:true}]),match)
 const normal=risk(lineup([{name:'normal'}]),match)
 assert.ok(compare(key,rec)>0);assert.ok(compare(rec,normal)>0)
 assert.ok(compare({...key,keyLoss:1}, {...rec,recommendedLoss:100})>0)
})
test('key wins conflicting flags and duplicate fixed operators count once',()=>{
 const r=risk(lineup([{name:'key',key:true,recommended:true},{name:'key',key:true}]),match)
 assert.equal(r.keyLoss,1); assert.equal(r.recommendedLoss,0)
})
test('banned key candidate in a satisfied choose-N group is not a missing core',()=>{
 const r=risk(lineup([],[{need:1,members:[{name:'key',key:true},{name:'available'}]}]),match)
 assert.equal(r.keyLoss,0);assert.equal(r.otherLoss,0);assert.equal(r.substitutes.length,1)
})
test('group shortages counted by deficit not banned candidate count',()=>{
 const r=risk(lineup([],[{need:2,members:[{name:'key',key:true},{name:'alt'},{name:'available'}]}]),match)
 assert.equal(r.keyLoss,1);assert.equal(r.groupWarnings.length,1)
})
test('no scan or incomplete scans are marked uncertain, no false safe label',()=>{
 assert.equal(risk(lineup(),{...match,source:'none'}).uncertain,true)
 assert.equal(risk(lineup(),{...match,banGroups:[]}).uncertain,true)
 assert.equal(risk(lineup(),match).uncertain,false)
})
