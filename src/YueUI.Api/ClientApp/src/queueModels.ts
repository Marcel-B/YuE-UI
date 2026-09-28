import { formatDuration, t } from './i18n'
import type { LyricsState, QueuedJob, SpeechTake, WorkerInfo } from './types'

/**
 * The large models that take turns in the memory (Queue/JobQueue.cs): YuE2 for songs and renders, the lyrics model in
 * LM Studio, separation plus Seed-VC for voice versions and stems, and the speech lab's text-to-speech models.
 */
export type Model = 'yue' | 'lyrics' | 'voice' | 'speech'

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

/** A take of the speech lab that is loading its model or speaking holds the memory (Speech/SpeechLab.cs). */
export function speaking(takes: SpeechTake[]): boolean {
  return takes.some((take) => take.stage === 'loading' || take.stage === 'speaking')
}

/** Which model holds the memory now, or null while none works. */
export function holderOf(
  worker: WorkerInfo,
  lyrics: LyricsState | null,
  versions: VoiceWork[],
  takes: SpeechTake[] = [],
): Model | null {
  if (speaking(takes)) {
    return 'speech'
  }
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
 */
export function waitReason(
  jobs: QueuedJob[],
  index: number,
  holder: Model | null,
  versions: VoiceWork[],
  windowSeconds: number | null,
  now: number,
): string {
  const job = jobs[index]!
  if (holder === 'speech') {
    // While a take speaks, JobQueue starts nothing (SpeechActivity.IsSpeaking).
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

/**
 * Why a take of the speech lab still waits (SpeechLab.WaitForMemoryAsync): its takes are spoken one at a time, in the
 * order they were asked for, each once YuE2, the lyrics model and the voices have let go of the memory.
 * @param index Its place among the takes in the works, the one being spoken included.
 */
export function speechWaitReason(index: number, holder: Model | null): string {
  if (holder === 'speech' || index > 0) {
    return t('waitTurn')
  }
  switch (holder) {
    case 'yue':
      return t('waitYue')
    case 'lyrics':
      return t('waitLyrics')
    case 'voice':
      return t('waitVoice')
    default:
      return t('waitStarting')
  }
}
