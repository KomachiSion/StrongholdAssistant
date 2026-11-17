import type { Lineup, LineupMember, MatchInfo } from './api/types'

const priority = (m: LineupMember) => m.key ? 2 : m.recommended ? 1 : 0
/** 成型阵容：任选组只在剩余候选不足 need 时计损失；可替代的禁用仅供提示。 */
export function evaluateLineup(lineup: Pick<Lineup, 'members' | 'goals' | 'groups'>, match: Pick<MatchInfo, 'bannedOperators' | 'covenants' | 'source' | 'awaitingDetail' | 'bansScanned' | 'banGroups'>) {
  const banned = new Set(match.bannedOperators)
  const unique = (ms: LineupMember[]) => [...new Map(ms.map(m => [m.name, m])).values()]
  const fixed = unique(lineup.members)
  const missing = fixed.filter(m => banned.has(m.name))
  const groupWarnings: string[] = []
  const substitutes: string[] = []
  let groupKeyLoss = 0, groupRecommendedLoss = 0, groupOtherLoss = 0
  for (const [i, g] of (lineup.groups ?? []).entries()) {
    const members = unique(g.members)
    if (!members.length) continue
    const available = members.filter(m => !banned.has(m.name))
    const need = Math.max(1, Math.min(members.length, Math.trunc(g.need) || 1))
    const deficit = Math.max(0, need - available.length)
    if (!deficit) {
      const names = members.filter(m => banned.has(m.name)).map(m => m.name)
      if (names.length) substitutes.push(`组${i + 1}：${names.join('、')}被禁，但仍满足任${need}`)
      continue
    }
    // 只计算补足缺口所需的最少受限候选；仍可用的关键成员不能抬高缺口等级。
    const missingPriorities = members.filter(m => banned.has(m.name)).map(priority).sort((a, b) => a - b).slice(0, deficit)
    groupKeyLoss += missingPriorities.filter(p => p === 2).length
    groupRecommendedLoss += missingPriorities.filter(p => p === 1).length
    groupOtherLoss += missingPriorities.filter(p => p === 0).length
    const p = Math.max(0, ...missingPriorities)
    groupWarnings.push(`组${i + 1}（${p === 2 ? '关键' : p === 1 ? '推荐' : '普通'}）：需${need}，剩${available.length}，缺${deficit}`)
  }
  const keyBans = missing.filter(m => priority(m) === 2).map(m => m.name)
  const recommendedBans = missing.filter(m => priority(m) === 1).map(m => m.name)
  const otherBans = missing.filter(m => priority(m) === 0).map(m => m.name)
  const keyLoss = keyBans.length + groupKeyLoss
  const recommendedLoss = recommendedBans.length + groupRecommendedLoss
  const otherLoss = otherBans.length + groupOtherLoss
  const affectedGoals = lineup.goals.filter(g => match.covenants.find(c => c.id === g.id)?.incomplete).map(g => g.id)
  const hasKeys = [...fixed, ...(lineup.groups ?? []).flatMap(g => g.members)].some(m => m.key)
  const uncertain = match.source === 'none' || !!match.awaitingDetail || !match.bansScanned || !match.banGroups.length
    || match.banGroups.some(g => !g.complete || g.slots.some(s => !s.name || s.inferred))
  const label = keyLoss ? '关键干员受限' : recommendedLoss ? '推荐干员受限' : affectedGoals.length ? '目标盟约受限' : otherLoss ? '普通成员受限' : '暂未发现禁用冲突'
  return { keyBans, recommendedBans, otherBans, keyLoss, recommendedLoss, otherLoss, affectedGoals, hasKeys, uncertain, label, groupWarnings, substitutes }
}
/** 字典序而非加权总和：关键 → 推荐 → 目标盟约 → 普通；人数不能抵消更重要的缺失。 */
export function compareLineupRisk(a: ReturnType<typeof evaluateLineup>, b: ReturnType<typeof evaluateLineup>) {
  return a.keyLoss - b.keyLoss || a.recommendedLoss - b.recommendedLoss || a.affectedGoals.length - b.affectedGoals.length || a.otherLoss - b.otherLoss || Number(b.hasKeys) - Number(a.hasKeys)
}
