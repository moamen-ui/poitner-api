import { memo } from 'react';
import { Card } from './Card';

export const TrackedCard = memo(function TrackedCard(p: any) {
  return <Card {...p} />;
});
