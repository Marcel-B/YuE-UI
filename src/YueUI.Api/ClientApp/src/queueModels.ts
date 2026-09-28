import { formatDuration, t } from './i18n'
import type { LyricsState, QueuedJob, WorkerInfo } from './types'

/**
 * The three large models that take turns in the memory (Queue/JobQueue.cs): YuE2 for songs and renders, the lyrics
 * model in LM Studio, and separation plus Seed-VC for voice versions and stems.
 */
export type Model = 'yue' | 'lyrics' | 'voice'

export function modelOf(job: QueuedJob): Model {
  return job.kind === 'lyrics' ? 'lyrics' : 'yue'
}

/**
 * A voice version or a stem separation: both wait in VoiceConverter's one queue and hold the memory as the "voice"
 * model while they run.
 */
export interface VoiceWork {
  stage: string
  createdAt: string
  finished: boolean
}

/** Which model holds the memory now, or null while none works. */
export function holderOf(worker: WorkerInfo, lyrics: LyricsState | null, versions: VoiceWork[]): Model | null {
  if (lyrics?.stage === 'writing') {
    return 'lyrics'
  }
  if (versions.some((v) => !v.finished && v.stage !== 'queued')) {
    return 'voice'
  }
  return worker.busy ? 'yue' : null
}

/**
 * Why a job still waits, in the words of the queue's own rules (JobQueue.TryStartAsync, MayPassLocked,
 * VersionOverdue): the model that holds the memory, the bundling window, or the job before it.
 * @param windowSeconds Queue:BundleWindow; null for a server from before it was sent.
 * @param speaking A take of the speech lab holds the memory; everything waits for it (Speech/SpeechLab.cs).
 */
export function waitReason(
  jobs: QueuedJob[],
  index: number,
  holder: Model | null,
  versions: VoiceWork[],
  windowSeconds: number | null,
  now: number,
  speaking = false,
): string {
  const job = jobs[index]!
  if (speaking) {
    return t('waitSpeech')
  }
  const left = (since: string) => (windowSeconds ?? 0) - (now - new Date(since).getTime()) / 1000

  if (modelOf(job) === 'lyrics') {
    if (holder === 'yue') {
      // Songs go ahead of a draft at the front while YuE2 is loaded anyway, until the draft has waited the window.
      const rest = index === 0 ? left(job.createdAt) : 0
      return rest > 0 ? t('waitYueBundle', { time: formatDuration(rest) }) : t('waitYue')
    }
    if (holder === 'voice') {
      return t('waitVoice')
    }
    if (holder === 'lyrics') {
      return t('waitDraft')
    }
    return index === 0 ? t('waitStarting') : t('waitTurn')
  }

  if (holder === 'lyrics') {
    return t('waitLyrics')
  }
  if (holder === 'voice') {
    return t('waitVoice')
  }
  const oldest = versions.filter((v) => v.stage === 'queued').sort((a, b) => a.createdAt.localeCompare(b.createdAt))[0]
  if (oldest && windowSeconds !== null && left(oldest.createdAt) <= 0) {
    return t('waitVersionFirst')
  }
  if (index > 0 && modelOf(jobs[0]!) === 'lyrics') {
    return t('waitBehindDraft')
  }
  return index === 0 ? t('waitStarting') : t('waitTurn')
}
