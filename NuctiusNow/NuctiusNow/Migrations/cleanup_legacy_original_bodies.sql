-- Optional one-time cleanup for rows created by the old prototype.
-- New code never persists extracted publisher article bodies.
UPDATE detail_news
SET full_body_text = NULL,
    ai_summary = NULL,
    body_source = 'Unknown'
WHERE LOWER(COALESCE(body_source, '')) = 'original';
