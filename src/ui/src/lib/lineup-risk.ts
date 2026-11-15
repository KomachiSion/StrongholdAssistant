import type { Lineup, MatchInfo } from './api/types'

/** 成型阵容的静态适配；不把未识别到的禁用等同于确定可用。 */
export function evaluateLineup(lineup: Pick<Lineup, 'members' | 'goals'>, match: Pick<MatchInfo, 'bannedOperators' | 'covenants' | 'source' | 'awaitingDetail' | 'bansScanned' | 'banGroups'>) {
  const banned = new Set(match.bannedOperators)
  const members = [...new Map(lineup.members.map(m => [m.name, m])).values()]
  const keyBans = members.filter(m => m.key && banned.has(m.name)).map(m => m.name)
  const otherBans = members.filter(m => !m.key && banned.has(m.name)).map(m => m.name)
  const affectedGoals = lineup.goals.filter(g => match.covenants.find(c => c.id === g.id)?.incomplete).map(g => g.id)
  const hasKeys = members.some(m => m.key)
  const uncertain = match.source === 'none' || match.awaitingDetail || !match.bansScanned || !match.banGroups.length
    || match.banGroups.some(g => !g.complete || g.slots.some(s => !s.name || s.inferred))
  // 先分风险组，再比较同组损失；不使用容易被人数抵消的加权总分。
  const rank = keyBans.length ? 3 : affectedGoals.length ? 2 : otherBans.length ? 1 : 0
  const label = keyBans.length ? '关键干员受限' : affectedGoals.length ? '目标盟约受限' : otherBans.length ? '普通成员受限' : '暂未发现禁用冲突'
  return { keyBans, otherBans, affectedGoals, hasKeys, uncertain, rank, label }
}

export function compareLineupRisk(a: ReturnType<typeof evaluateLineup>, b: ReturnType<typeof evaluateLineup>) {
  return a.rank - b.rank || Number(b.hasKeys) - Number(a.hasKeys) || a.keyBans.length - b.keyBans.length || a.affectedGoals.length - b.affectedGoals.length || a.otherBans.length - b.otherBans.length
}
